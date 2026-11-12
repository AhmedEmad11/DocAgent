namespace DocAgent.Core.Entities;

public class Document
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public required string FileName { get; set; }
    public required string ContentType { get; set; }
    public long SizeBytes { get; set; }
    public int? PageCount { get; set; }
    public int ChunkCount { get; set; }
    public DocumentStatus Status { get; set; }
    public string? FailureReason { get; set; }
    /// <summary>Set when extraction was best effort (SRS DOC-11 / D31); null otherwise.</summary>
    public string? ExtractionNotice { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
