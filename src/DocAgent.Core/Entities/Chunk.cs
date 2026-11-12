using Pgvector;

namespace DocAgent.Core.Entities;

public class Chunk
{
    /// <summary>Embedding dimensionality (multilingual-e5-small; SRS section 7).</summary>
    public const int EmbeddingDimensions = 384;

    public Guid Id { get; set; }
    public Guid DocumentId { get; set; }

    /// <summary>Denormalized owner, used for tenant filtering without a join.</summary>
    public Guid UserId { get; set; }
    public int Ordinal { get; set; }
    public required string Text { get; set; }
    public int? PageNumber { get; set; }
    public string? Section { get; set; }
    public int TokenCount { get; set; }
    public required string EmbeddingModel { get; set; }
    public required Vector Embedding { get; set; }
}
