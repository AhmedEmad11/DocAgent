using System.Diagnostics;
using System.Text;
using DocAgent.Core.Entities;
using DocAgent.Core.Retrieval;
using DocAgent.Infrastructure.Data;
using DocAgent.Infrastructure.Llm;
using DocAgent.Infrastructure.Qa;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocAgent.Infrastructure.Agents;

/// <summary>A finished summary or comparison. <see cref="Text"/> carries [n] markers that refer to the run's <see cref="SourceRegistry"/>.</summary>
public sealed record WorkflowResult(string Text, bool Incomplete, IReadOnlyList<Guid> DocumentIds);

/// <summary>
/// Summarize (QA-6) and compare (QA-7) by map-reduce over the ordered chunks of a document (its full text is derived
/// from the chunks, plan phase 2). Every map call holds at most <c>Agent:MapBudgetTokens</c> of source text so it fits
/// the model context; chunks are numbered through the <see cref="SourceRegistry"/> so citations map back to page/section.
/// All reads go through the tenant-filtered <see cref="AppDbContext"/>.
/// </summary>
public sealed class DocumentWorkflows(
    AppDbContext db,
    IOptions<AgentOptions> options,
    IOptions<QaOptions> qaOptions,
    ILogger<DocumentWorkflows> logger)
{
    private const int MaxBulletsPerPart = 8;
    private const int MaxBulletsFinal = 10;

    /// <summary>The caller's document with that id or exact file name (Ready or not), or null. Another user's document is null.</summary>
    public async Task<Document?> FindAsync(string reference, CancellationToken ct)
    {
        reference = reference.Trim();
        if (reference.Length == 0 || reference.Length > 600)
        {
            return null;
        }

        if (Guid.TryParse(reference, out var id))
        {
            return await db.Documents.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);
        }

        var matches = await db.Documents.AsNoTracking().Where(d => d.FileName == reference).Take(2).ToListAsync(ct);
        return matches.Count == 1 ? matches[0] : null; // an ambiguous name is not guessed
    }

    public async Task<List<RetrievedChunk>> LoadChunksAsync(Document doc, CancellationToken ct) =>
        await db.Chunks.AsNoTracking()
            .Where(c => c.DocumentId == doc.Id)
            .OrderBy(c => c.Ordinal)
            .Select(c => new RetrievedChunk(c.Id, c.DocumentId, doc.FileName, c.PageNumber, c.Section, c.Ordinal, c.Text, c.TokenCount, 0))
            .ToListAsync(ct);

    public async Task<WorkflowResult> SummarizeAsync(
        Document doc, string? focus, SourceRegistry registry, LlmRoute route, TimeSpan timeout, CancellationToken ct)
    {
        using var activity = CountedFunction.ActivitySource.StartActivity("agent.summarize");
        var chunks = await LoadChunksAsync(doc, ct);
        activity?.SetTag("workflow.chunks", chunks.Count);
        var language = Language(focus, chunks);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        var partials = new List<string>();
        try
        {
            var text = await MapReduceAsync(chunks, focus, language, topics: false, registry, route, partials, deadline.Token);
            return new WorkflowResult(text, false, [doc.Id]);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            return PartialOrTimeout(partials, doc, timeout);
        }
    }

    public async Task<WorkflowResult> CompareAsync(
        Document a, Document b, string? focus, SourceRegistry registry, LlmRoute route, TimeSpan timeout, CancellationToken ct)
    {
        using var activity = CountedFunction.ActivitySource.StartActivity("agent.compare");
        var chunksA = await LoadChunksAsync(a, ct);
        var chunksB = await LoadChunksAsync(b, ct);
        activity?.SetTag("workflow.chunks", chunksA.Count + chunksB.Count);
        var language = Language(focus, [.. chunksA, .. chunksB]);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        List<string> partialsA = [], partialsB = [];
        try
        {
            // Each document is condensed into topic-labelled key points; the comparison then aligns the two digests.
            var digestA = await MapReduceAsync(chunksA, focus, language, topics: true, registry, route, partialsA, deadline.Token);
            var digestB = await MapReduceAsync(chunksB, focus, language, topics: true, registry, route, partialsB, deadline.Token);
            var system = WorkflowPrompts.CompareSystem(language, focus);
            var user = WorkflowPrompts.CompareUser(a.FileName, digestA, b.FileName, digestB);
            var text = await ChatAsync(route, system, user, deadline.Token);
            return new WorkflowResult(Clean(text, registry), false, [a.Id, b.Id]);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            var parts = new List<string>();
            parts.AddRange(partialsA.Select(p => $"{a.FileName}:\n{p}"));
            parts.AddRange(partialsB.Select(p => $"{b.FileName}:\n{p}"));
            if (parts.Count == 0)
            {
                throw new QaTimeoutException("The comparison took longer than the time limit.");
            }

            logger.LogWarning("Comparison hit the time limit; returning the partial results ({Parts} parts)", parts.Count);
            return new WorkflowResult(Clean(string.Join("\n\n", parts), registry), true, [a.Id, b.Id]);
        }
    }

    private WorkflowResult PartialOrTimeout(List<string> partials, Document doc, TimeSpan timeout)
    {
        if (partials.Count == 0)
        {
            throw new QaTimeoutException($"The summary took longer than {timeout.TotalSeconds:F0} seconds.");
        }

        logger.LogWarning("Summary hit the time limit; returning {Parts} partial results", partials.Count);
        return new WorkflowResult(string.Join("\n", partials), true, [doc.Id]);
    }

    /// <summary>Map every batch of chunks to bullet points, then reduce the bullets (in groups, repeatedly) into one text.</summary>
    internal async Task<string> MapReduceAsync(
        IReadOnlyList<RetrievedChunk> chunks, string? focus, string language, bool topics,
        SourceRegistry registry, LlmRoute route, List<string> partials, CancellationToken ct)
    {
        var settings = options.Value;
        var batches = TokenBatcher.Group(chunks, c => c.TokenCount, settings.MapBudgetTokens);
        if (batches.Count == 0)
        {
            return "";
        }

        if (batches.Count == 1)
        {
            // Small document: one call produces the final text.
            var sources = Register(batches[0], registry);
            var text = await ChatAsync(route, WorkflowPrompts.MapSystem(language, focus, topics, MaxBulletsFinal), WorkflowPrompts.MapUser(sources), ct);
            var cleaned = Clean(text, registry, sources.Select(s => s.Index));
            partials.Add(cleaned);
            return cleaned;
        }

        foreach (var batch in batches)
        {
            ct.ThrowIfCancellationRequested();
            var sources = Register(batch, registry);
            var text = await ChatAsync(route, WorkflowPrompts.MapSystem(language, focus, topics, MaxBulletsPerPart), WorkflowPrompts.MapUser(sources), ct);
            partials.Add(Clean(text, registry, sources.Select(s => s.Index)));
        }

        var current = partials.ToList();
        while (true)
        {
            var groups = TokenBatcher.Group(current, TokenBatcher.EstimateTokens, settings.MapBudgetTokens);
            var last = groups.Count == 1;
            var next = new List<string>();
            foreach (var group in groups)
            {
                ct.ThrowIfCancellationRequested();
                if (group.Count == 1 && !last)
                {
                    next.Add(group[0]);
                    continue;
                }

                var merged = await ChatAsync(
                    route, WorkflowPrompts.ReduceSystem(language, focus, topics, last ? MaxBulletsFinal : MaxBulletsPerPart),
                    WorkflowPrompts.ReduceUser(group), ct);
                next.Add(Clean(merged, registry));
            }

            if (last)
            {
                return next[0];
            }

            if (next.Count >= current.Count)
            {
                // No progress (every group a single part): force a final merge of what fits.
                return string.Join("\n", next);
            }

            current = next;
        }
    }

    private static List<(int Index, RetrievedChunk Chunk)> Register(IEnumerable<RetrievedChunk> batch, SourceRegistry registry) =>
        [.. batch.Select(c => (registry.Register(c), c))];

    private async Task<string> ChatAsync(LlmRoute route, string system, string user, CancellationToken ct)
    {
        var chatOptions = route.Options?.Clone() ?? new ChatOptions();
        chatOptions.Temperature = qaOptions.Value.Temperature;
        chatOptions.MaxOutputTokens = options.Value.WorkflowMaxOutputTokens;
        var started = Stopwatch.GetTimestamp();
        try
        {
            var response = await route.Client.GetResponseAsync(
                [new ChatMessage(ChatRole.System, system), new ChatMessage(ChatRole.User, user)], chatOptions, ct);
            logger.LogInformation("Workflow model call finished in {ElapsedMs:F0} ms", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return response.Text?.Trim() ?? "";
        }
        catch (Exception ex) when (QaService.IsConnectionFailure(ex) && !ct.IsCancellationRequested)
        {
            logger.LogWarning("Workflow: the language model is unavailable ({ExceptionType})", ex.GetType().Name);
            throw new LlmUnavailableException("The language model is unavailable.", ex);
        }
    }

    /// <summary>Removes markers that point at no known source, or (when given) outside the allowed numbers.</summary>
    private static string Clean(string text, SourceRegistry registry, IEnumerable<int>? allowed = null)
    {
        var set = allowed?.ToHashSet();
        var count = registry.Count;
        return CitationParser.Parse(text, n => n >= 1 && n <= count && (set is null || set.Contains(n))).Answer.Trim();
    }

    internal static string Language(string? focus, IReadOnlyList<RetrievedChunk> chunks)
    {
        if (!string.IsNullOrWhiteSpace(focus) && focus.Any(char.IsLetter))
        {
            return IsArabic(focus) ? "Arabic" : "English";
        }

        var sample = new StringBuilder();
        foreach (var chunk in chunks.Take(3))
        {
            sample.Append(chunk.Text.AsSpan(0, Math.Min(600, chunk.Text.Length)));
        }

        return IsArabic(sample.ToString()) ? "Arabic" : "English";
    }

    private static bool IsArabic(string text)
    {
        int arabic = 0, letters = 0;
        foreach (var c in text)
        {
            if (char.IsLetter(c))
            {
                letters++;
                if (c is >= '؀' and <= 'ۿ')
                {
                    arabic++;
                }
            }
        }

        return letters > 0 && arabic * 2 > letters;
    }
}
