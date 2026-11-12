using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocAgent.Core.Retrieval;
using DocAgent.Infrastructure.Llm;
using DocAgent.Infrastructure.Retrieval;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocAgent.Infrastructure.Qa;

public sealed record QaCitation(int Index, Guid DocumentId, string FileName, int? PageNumber, string? Section, string Quote);

public sealed record QaAnswer(string Answer, bool NotFound, IReadOnlyList<QaCitation> Citations, LlmRouteInfo Route);

/// <summary>The language model could not be reached (connection refused, DNS, model missing, ...).</summary>
public sealed class LlmUnavailableException(string message, Exception inner) : Exception(message, inner);

/// <summary>The question did not finish within <c>Qa:TimeoutSeconds</c>.</summary>
public sealed class QaTimeoutException(string message) : Exception(message);

/// <summary>Single-pass RAG: retrieve, prompt once, parse (SRS QA-1, QA-8, QA-9, QA-12, QA-14).</summary>
public sealed class QaService(
    IRetriever retriever,
    ILlmClientFactory llmFactory,
    IOptions<RetrievalOptions> retrievalOptions,
    IOptions<QaOptions> qaOptions,
    ILogger<QaService> logger)
{
    public const string NotFoundMessageEnglish = "I could not find the answer to this question in your documents.";
    public const string NotFoundMessageArabic = "لم أجد إجابة هذا السؤال في مستنداتك.";

    private static readonly ActivitySource ActivitySource = new("DocAgent.Qa");

    private sealed record ModelReply(
        [property: JsonPropertyName("answer")] string? Answer,
        [property: JsonPropertyName("notFound")] bool NotFound);

    public async Task<QaAnswer> AskAsync(string question, IReadOnlyCollection<Guid>? documentIds, CancellationToken ct)
    {
        var qa = qaOptions.Value;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(qa.TimeoutSeconds));
        try
        {
            return await AskCoreAsync(question, documentIds, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new QaTimeoutException($"The question took longer than {qa.TimeoutSeconds} seconds to answer.");
        }
    }

    private async Task<QaAnswer> AskCoreAsync(string question, IReadOnlyCollection<Guid>? documentIds, CancellationToken ct)
    {
        var qa = qaOptions.Value;
        using var activity = ActivitySource.StartActivity("qa.ask");
        var started = Stopwatch.GetTimestamp();
        var route = await llmFactory.GetRouteAsync(ct);

        var sources = await retriever.SearchAsync(question, documentIds, retrievalOptions.Value.TopK, ct);
        if (sources.Count == 0)
        {
            // Nothing relevant enough: answer without spending an LLM call (SRS QA-9).
            logger.LogInformation("Q&A: no source passed the relevance threshold; model not called");
            return new QaAnswer(NotFoundMessage(question), true, [], route.Info);
        }

        var options = route.Options?.Clone() ?? new ChatOptions();
        options.Temperature = qa.Temperature;
        options.MaxOutputTokens = qa.MaxOutputTokens;
        options.ResponseFormat = ChatResponseFormat.ForJsonSchema<ModelReply>(schemaName: "reply");

        ChatResponse response;
        try
        {
            response = await route.Client.GetResponseAsync(
                [
                    new ChatMessage(ChatRole.System, QaPromptBuilder.SystemPrompt),
                    new ChatMessage(ChatRole.User, QaPromptBuilder.BuildUserMessage(question, sources)),
                ],
                options, ct);
        }
        catch (Exception ex) when (IsConnectionFailure(ex) && !ct.IsCancellationRequested)
        {
            logger.LogWarning("Q&A: the language model is unavailable ({ExceptionType})", ex.GetType().Name);
            throw new LlmUnavailableException("The language model is unavailable.", ex);
        }

        var reply = ParseReply(response.Text);
        var answerText = reply.Answer?.Trim();
        if (string.IsNullOrEmpty(answerText))
        {
            logger.LogWarning("Q&A: the model returned an empty answer");
            return new QaAnswer(NotFoundMessage(question), true, [], route.Info);
        }

        var parsed = CitationParser.Parse(answerText, sources.Count);
        var citations = reply.NotFound
            ? []
            : parsed.Indexes.Select(i => ToCitation(i, sources[i - 1], question, parsed.Answer, qa.QuoteMaxChars)).ToList();

        activity?.SetTag("qa.sources", sources.Count);
        activity?.SetTag("qa.citations", citations.Count);
        activity?.SetTag("qa.not_found", reply.NotFound);
        logger.LogInformation(
            "Q&A answered: {Sources} sources, {Citations} citations, notFound={NotFound}, {InputTokens} input / {OutputTokens} output tokens, {ElapsedMs:F0} ms",
            sources.Count, citations.Count, reply.NotFound, response.Usage?.InputTokenCount, response.Usage?.OutputTokenCount,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return new QaAnswer(parsed.Answer, reply.NotFound, citations, route.Info);
    }

    private static QaCitation ToCitation(int index, RetrievedChunk source, string question, string answer, int maxChars)
    {
        // The sentence of the answer that carries the marker tells which part of the source matters.
        var sentence = answer.Split(['.', '!', '?', '\n', '؟']).FirstOrDefault(s => s.Contains($"[{index}") || s.Contains($", {index}")) ?? "";
        var quote = QuoteTrimmer.Trim(source.Text, question + " " + sentence, maxChars);
        return new QaCitation(index, source.DocumentId, source.FileName, source.PageNumber, source.Section, quote);
    }

    /// <summary>Reads the model's JSON reply; a reply that is not valid JSON is used as plain answer text.</summary>
    internal static (string? Answer, bool NotFound) ParseReply(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (null, false);
        }

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            try
            {
                var reply = JsonSerializer.Deserialize<ModelReply>(text[start..(end + 1)], JsonSerializerOptions.Web);
                if (reply is not null)
                {
                    return (reply.Answer, reply.NotFound);
                }
            }
            catch (JsonException)
            {
                // fall through to plain text
            }
        }

        return (text, false);
    }

    internal static string NotFoundMessage(string question) =>
        question.Any(c => c is >= '؀' and <= 'ۿ') ? NotFoundMessageArabic : NotFoundMessageEnglish;

    private static bool IsConnectionFailure(Exception ex) =>
        ex is HttpRequestException or System.Net.Sockets.SocketException or OllamaSharp.Models.Exceptions.OllamaException
        || ex.InnerException is HttpRequestException or System.Net.Sockets.SocketException;
}
