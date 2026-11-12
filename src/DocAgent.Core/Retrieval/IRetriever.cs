namespace DocAgent.Core.Retrieval;

/// <summary>A chunk returned by a search. <see cref="Score"/> is the cosine similarity to the query (0..1, higher is closer).</summary>
public sealed record RetrievedChunk(
    Guid ChunkId,
    Guid DocumentId,
    string FileName,
    int? PageNumber,
    string? Section,
    int Ordinal,
    string Text,
    int TokenCount,
    double Score);

public interface IRetriever
{
    /// <summary>
    /// Finds the chunks most relevant to <paramref name="query"/> among the current user's Ready documents,
    /// optionally restricted to <paramref name="documentIds"/>. Results are best first.
    /// </summary>
    Task<IReadOnlyList<RetrievedChunk>> SearchAsync(
        string query, IReadOnlyCollection<Guid>? documentIds, int topK, CancellationToken ct = default);
}
