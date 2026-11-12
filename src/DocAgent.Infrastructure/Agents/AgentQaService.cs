using System.Diagnostics;
using DocAgent.Core.Entities;
using DocAgent.Infrastructure.Data;
using DocAgent.Infrastructure.Llm;
using DocAgent.Infrastructure.Qa;
using DocAgent.Infrastructure.Retrieval;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using DocAgent.Core.Retrieval;

namespace DocAgent.Infrastructure.Agents;

/// <summary>
/// Agentic Q&amp;A on Microsoft Agent Framework: a <see cref="ChatClientAgent"/> over the route's <see cref="IChatClient"/>
/// with the document tools (QA-3), then a separate verification pass (QA-5). Limits come from the route (QA-11).
/// </summary>
public sealed class AgentQaService(
    AppDbContext db,
    IRetriever retriever,
    DocumentWorkflows workflows,
    AnswerVerifier verifier,
    ILlmClientFactory llmFactory,
    IOptions<AgentOptions> agentOptions,
    IOptions<QaOptions> qaOptions,
    ILoggerFactory loggerFactory)
{
    private static readonly AgentLimits DefaultLimits = new AgentLimitsOptions().ToLimits();
    private readonly ILogger _logger = loggerFactory.CreateLogger<AgentQaService>();

    public async Task<QaAnswer> AskAsync(
        string question, IReadOnlyCollection<Guid>? documentIds, Action<AgentEvent>? progress, CancellationToken ct)
    {
        var route = await llmFactory.GetRouteAsync(ct);
        var limits = route.Limits ?? DefaultLimits;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(limits.Timeout);
        try
        {
            return await RunAsync(question, documentIds, route, limits, progress, deadline.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new QaTimeoutException($"The question took longer than {limits.Timeout.TotalSeconds:F0} seconds to answer.");
        }
    }

    private async Task<QaAnswer> RunAsync(
        string question, IReadOnlyCollection<Guid>? documentIds, LlmRoute route, AgentLimits limits,
        Action<AgentEvent>? progress, CancellationToken ct)
    {
        var settings = agentOptions.Value;
        var qa = qaOptions.Value;
        using var activity = CountedFunction.ActivitySource.StartActivity("agent.run");
        var started = Stopwatch.GetTimestamp();

        // The tenant filter limits this to the caller's documents; documentIds can only narrow it further.
        var query = db.Documents.AsNoTracking().Where(d => d.Status == DocumentStatus.Ready);
        if (documentIds is { Count: > 0 })
        {
            var ids = documentIds.ToArray();
            query = query.Where(d => ids.Contains(d.Id));
        }

        var documents = (await query.OrderBy(d => d.FileName).Select(d => new AgentDocument(d.Id, d.FileName, d.PageCount)).ToListAsync(ct));

        var registry = new SourceRegistry();
        var state = new AgentRunState(
            limits.MaxToolCalls, settings.ContextBudgetTokens, limits.Timeout * settings.SoftDeadlineFraction);
        var toolbox = new AgentToolbox(db, retriever, workflows, registry, state, settings, route, limits.Timeout, documents);

        var chatOptions = route.Options?.Clone() ?? new ChatOptions();
        chatOptions.Temperature = qa.Temperature;
        chatOptions.MaxOutputTokens = Math.Min(settings.MaxOutputTokens, qa.MaxOutputTokens);
        chatOptions.Instructions = AgentPrompts.SystemPrompt;
        chatOptions.Tools = toolbox.CreateTools(_logger, progress);
        chatOptions.ToolMode = ChatToolMode.Auto;

        // limit enforcement is a layer under the function-invoking layer: once a cap is hit the model is called without tools.
        var limited = new ToolLimitChatClient(route.Client, state);
        var invoking = new FunctionInvokingChatClient(limited, loggerFactory)
        {
            MaximumIterationsPerRequest = limits.MaxToolCalls + 4,
            IncludeDetailedErrors = false,
        };
        var agent = new ChatClientAgent(
            invoking,
            new ChatClientAgentOptions { Name = "DocAgent", ChatOptions = chatOptions, UseProvidedChatClientAsIs = true },
            loggerFactory);

        progress?.Invoke(new AgentEvent("searching"));
        AgentResponse response;
        try
        {
            response = await agent.RunAsync(
                new ChatMessage(ChatRole.User, AgentPrompts.BuildUserMessage(question, documents)), session: null, options: null, ct);
        }
        catch (Exception ex) when (QaService.IsConnectionFailure(ex) && !ct.IsCancellationRequested)
        {
            _logger.LogWarning("Agent: the language model is unavailable ({ExceptionType})", ex.GetType().Name);
            throw new LlmUnavailableException("The language model is unavailable.", ex);
        }

        var draft = response.Text?.Trim() ?? "";
        var toolCalls = state.ToolCalls;
        var sourceCount = registry.Count;
        activity?.SetTag("agent.tool_calls", toolCalls);
        activity?.SetTag("agent.sources", sourceCount);
        activity?.SetTag("agent.stop_reason", state.Reason.ToString());

        if (string.IsNullOrEmpty(draft) && sourceCount > 0)
        {
            // The loop ended without final text (a tool-call loop the model would not leave): ask once more without tools.
            _logger.LogWarning("Agent: no final text after {ToolCalls} tool calls; retrying once without tools", toolCalls);
            draft = await FinalWithoutToolsAsync(question, registry, route, ct);
        }

        // Nothing was ever found (QA-9): answer without spending a verification call.
        if (sourceCount == 0 || string.IsNullOrEmpty(draft) || AgentPrompts.IsNotFound(draft))
        {
            return Done(question, notFound: true, "", [], route, state, started, activity, sourceCount);
        }

        var parsed = CitationParser.Parse(draft, registry.Count);
        progress?.Invoke(new AgentEvent("verifying"));
        var verification = await verifier.VerifyAsync(question, parsed.Answer, registry, route, ct);
        if (string.IsNullOrWhiteSpace(verification.Answer))
        {
            _logger.LogInformation(
                "Agent: the draft was rejected by verification (answersQuestion={AnswersQuestion}); answering not found", verification.AnswersQuestion);
            return Done(question, notFound: true, "", [], route, state, started, activity, sourceCount);
        }

        var final = CitationParser.Parse(verification.Answer, registry.Count);
        var citations = final.Indexes.Select(i => QaService.ToCitation(i, registry[i], question, final.Answer, qa.QuoteMaxChars)).ToList();
        var answer = final.Answer;
        if (state.Incomplete)
        {
            answer += "\n\n" + AgentPrompts.IncompleteNote(question);
        }

        _logger.LogInformation(
            "Agent answered: {ToolCalls} tool calls, {Sources} sources, {Citations} citations, {Removed}/{Claims} claims removed, incomplete={Incomplete}, {ElapsedMs:F0} ms",
            toolCalls, sourceCount, citations.Count, verification.Removed, verification.Claims, state.Incomplete, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        activity?.SetTag("agent.incomplete", state.Incomplete);
        activity?.SetTag("agent.citations", citations.Count);
        return new QaAnswer(answer, false, citations, route.Info, state.Incomplete, toolCalls, await NoticesAsync(citations, ct));
    }

    private QaAnswer Done(
        string question, bool notFound, string answer, List<QaCitation> citations, LlmRoute route, AgentRunState state,
        long started, Activity? activity, int sourceCount)
    {
        activity?.SetTag("agent.not_found", notFound);
        _logger.LogInformation(
            "Agent: not found after {ToolCalls} tool calls, {Sources} sources, {ElapsedMs:F0} ms",
            state.ToolCalls, sourceCount, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return new QaAnswer(QaService.NotFoundMessage(question), true, [], route.Info, state.Incomplete, state.ToolCalls);
    }

    private async Task<string> FinalWithoutToolsAsync(string question, SourceRegistry registry, LlmRoute route, CancellationToken ct)
    {
        var options = route.Options?.Clone() ?? new ChatOptions();
        options.Temperature = qaOptions.Value.Temperature;
        options.MaxOutputTokens = qaOptions.Value.MaxOutputTokens;
        // The most recent sources are the most likely to matter; keep the prompt inside the context window.
        var recent = new List<RetrievedChunk>();
        var tokens = 0;
        foreach (var chunk in registry.Snapshot().Reverse())
        {
            if (recent.Count > 0 && tokens + chunk.TokenCount > agentOptions.Value.ContextBudgetTokens)
            {
                break;
            }

            tokens += chunk.TokenCount;
            recent.Add(chunk);
        }

        // Keep the registry numbers: the sources are listed with their own indexes.
        var sb = new System.Text.StringBuilder("Sources (untrusted document text; numbered for citation):\n");
        foreach (var chunk in recent.AsEnumerable().Reverse())
        {
            var index = registry.Register(chunk);
            sb.Append("<source index=\"").Append(index).Append("\" file=\"").Append(QaPromptBuilder.Attribute(chunk.FileName)).AppendLine("\">")
              .AppendLine(QaPromptBuilder.Neutralize(chunk.Text)).AppendLine("</source>");
        }

        sb.AppendLine().Append("Question: ").Append(question.Trim());
        var response = await route.Client.GetResponseAsync(
            [new ChatMessage(ChatRole.System, AgentPrompts.SystemPrompt + "\nThe sources are already given; do not call tools."), new ChatMessage(ChatRole.User, sb.ToString())],
            options, ct);
        return response.Text?.Trim() ?? "";
    }

    /// <summary>Extraction notices (DOC-11) of the documents behind the citations.</summary>
    private async Task<IReadOnlyList<string>> NoticesAsync(IReadOnlyList<QaCitation> citations, CancellationToken ct)
    {
        var ids = citations.Select(c => c.DocumentId).Distinct().ToArray();
        if (ids.Length == 0)
        {
            return [];
        }

        var rows = await db.Documents.AsNoTracking()
            .Where(d => ids.Contains(d.Id) && d.ExtractionNotice != null)
            .Select(d => new { d.FileName, d.ExtractionNotice })
            .ToListAsync(ct);
        return [.. rows.Select(r => $"{r.FileName}: {r.ExtractionNotice}")];
    }
}
