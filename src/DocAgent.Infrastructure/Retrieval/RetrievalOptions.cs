namespace DocAgent.Infrastructure.Retrieval;

public sealed class RetrievalOptions
{
    public const string SectionName = "Retrieval";

    /// <summary>Number of chunks returned when the caller does not ask for a specific count.</summary>
    public int TopK { get; set; } = 8;

    /// <summary>Minimum cosine similarity (1 - cosine distance) for a chunk to be returned.</summary>
    public double MinScore { get; set; } = 0.80;

    /// <summary>Upper bound on the summed token count of the returned chunks (the first chunk is always kept).</summary>
    public int MaxContextTokens { get; set; } = 3000;

    /// <summary>Fuse vector search with Postgres full-text search by reciprocal rank fusion.</summary>
    public bool Hybrid { get; set; } = true;

    /// <summary>Minimum cosine similarity for a chunk found only by full-text search.</summary>
    public double FullTextMinScore { get; set; } = 0.75;

    /// <summary>Candidates fetched from each source before fusion, as a multiple of topK.</summary>
    public int CandidateMultiplier { get; set; } = 4;

    /// <summary>The constant k of reciprocal rank fusion: score = sum of 1 / (k + rank).</summary>
    public int RrfK { get; set; } = 60;
}
