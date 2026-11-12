using System.Diagnostics;
using DocAgent.Core.Entities;
using DocAgent.Core.Retrieval;
using DocAgent.Infrastructure.Data;
using DocAgent.Infrastructure.Embeddings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace DocAgent.Infrastructure.Retrieval;

/// <summary>
/// Vector search (pgvector cosine, HNSW) optionally fused with Postgres full-text search (reciprocal rank fusion).
/// Reads go through <see cref="AppDbContext"/>, so the per-user query filter always applies (SRS QA-2, SEC-3).
/// </summary>
public sealed class Retriever(
    AppDbContext db,
    IEmbeddingGenerator<string, Embedding<float>> embedder,
    IOptions<RetrievalOptions> options,
    ILogger<Retriever> logger) : IRetriever
{
    public static readonly ActivitySource ActivitySource = new("DocAgent.Retrieval");

    private sealed record Row(
        Guid ChunkId, Guid DocumentId, string FileName, int? PageNumber, string? Section, int Ordinal, string Text, int TokenCount, double Distance)
    {
        public RetrievedChunk ToChunk() => new(ChunkId, DocumentId, FileName, PageNumber, Section, Ordinal, Text, TokenCount, 1 - Distance);
    }

    public async Task<IReadOnlyList<RetrievedChunk>> SearchAsync(
        string query, IReadOnlyCollection<Guid>? documentIds, int topK, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var settings = options.Value;
        topK = topK > 0 ? topK : settings.TopK;
        using var activity = ActivitySource.StartActivity("retrieval.search");
        var started = Stopwatch.GetTimestamp();

        var embedding = await embedder.GenerateAsync([EmbeddingOptions.QueryPrefix + query], cancellationToken: ct);
        var queryVector = new Vector(embedding[0].Vector.ToArray());
        var candidates = Math.Max(topK, topK * settings.CandidateMultiplier);
        var ftsTerms = settings.Hybrid ? FullTextQuery.Build(query) : null;
        var ids = documentIds is { Count: > 0 } ? documentIds.ToArray() : null;

        var (vectorRows, ftsRows) = await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            // Own transaction so SET LOCAL applies: iterative scan keeps scanning the HNSW index until enough rows
            // survive the user/document/status filters (otherwise the filters would silently thin out the result).
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlRawAsync("SET LOCAL hnsw.iterative_scan = strict_order", ct);

            var documents = ReadyDocuments(ids);
            var vector = await db.Chunks
                .Join(documents, c => c.DocumentId, d => d.Id, (c, d) => new { Chunk = c, Doc = d })
                .OrderBy(x => x.Chunk.Embedding.CosineDistance(queryVector))
                .Select(x => new Row(x.Chunk.Id, x.Doc.Id, x.Doc.FileName, x.Chunk.PageNumber, x.Chunk.Section, x.Chunk.Ordinal,
                    x.Chunk.Text, x.Chunk.TokenCount, x.Chunk.Embedding.CosineDistance(queryVector)))
                .Take(candidates)
                .ToListAsync(ct);

            List<Row> fts = [];
            if (ftsTerms is not null)
            {
                // Strong lexical matches only: all the question's words, or all its identifiers (codes, numbers).
                var all = ftsTerms.All;
                var identifiers = ftsTerms.Identifiers ?? ftsTerms.All;
                fts = await db.Chunks
                    .Where(c => EF.Functions.ToTsVector("simple", c.Text).Matches(EF.Functions.WebSearchToTsQuery("simple", all))
                        || EF.Functions.ToTsVector("simple", c.Text).Matches(EF.Functions.WebSearchToTsQuery("simple", identifiers)))
                    .Join(documents, c => c.DocumentId, d => d.Id, (c, d) => new { Chunk = c, Doc = d })
                    .OrderByDescending(x => EF.Functions.ToTsVector("simple", x.Chunk.Text).Matches(EF.Functions.WebSearchToTsQuery("simple", all)))
                    .ThenByDescending(x => EF.Functions.ToTsVector("simple", x.Chunk.Text).RankCoverDensity(EF.Functions.WebSearchToTsQuery("simple", all)))
                    .Select(x => new Row(x.Chunk.Id, x.Doc.Id, x.Doc.FileName, x.Chunk.PageNumber, x.Chunk.Section, x.Chunk.Ordinal,
                        x.Chunk.Text, x.Chunk.TokenCount, x.Chunk.Embedding.CosineDistance(queryVector)))
                    .Take(candidates)
                    .ToListAsync(ct);
            }

            await tx.CommitAsync(ct);
            return (vector, fts);
        });

        // Vector hits must reach MinScore; full-text-only hits get their own (slightly lower) bar because an exact
        // term match is evidence on its own.
        var vectorHits = vectorRows.Where(r => 1 - r.Distance >= settings.MinScore).ToList();
        var ftsHits = ftsRows.Where(r => 1 - r.Distance >= settings.FullTextMinScore).ToList();

        IEnumerable<Row> ordered;
        if (ftsTerms is null)
        {
            ordered = vectorHits;
        }
        else
        {
            var byId = vectorHits.Concat(ftsHits).GroupBy(r => r.ChunkId).ToDictionary(g => g.Key, g => g.First());
            ordered = ReciprocalRankFusion
                .Fuse([vectorHits.Select(r => r.ChunkId).ToList(), ftsHits.Select(r => r.ChunkId).ToList()], settings.RrfK)
                .Select(f => byId[f.Item]);
        }

        var result = new List<RetrievedChunk>();
        var tokens = 0;
        foreach (var row in ordered.Take(topK))
        {
            if (result.Count > 0 && tokens + row.TokenCount > settings.MaxContextTokens)
            {
                break;
            }

            tokens += row.TokenCount;
            result.Add(row.ToChunk());
        }

        var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        activity?.SetTag("retrieval.hybrid", ftsTerms is not null);
        activity?.SetTag("retrieval.results", result.Count);
        activity?.SetTag("retrieval.vector_candidates", vectorRows.Count);
        activity?.SetTag("retrieval.fulltext_candidates", ftsRows.Count);
        logger.LogInformation(
            "Retrieval returned {Count} chunks ({VectorCandidates} vector, {FullTextCandidates} full-text candidates, {Tokens} tokens) in {ElapsedMs:F0} ms",
            result.Count, vectorRows.Count, ftsRows.Count, tokens, elapsedMs);
        return result;
    }

    private IQueryable<Document> ReadyDocuments(Guid[]? documentIds)
    {
        var documents = db.Documents.Where(d => d.Status == DocumentStatus.Ready);
        return documentIds is null ? documents : documents.Where(d => documentIds.Contains(d.Id));
    }
}
