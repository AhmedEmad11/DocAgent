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
    public DateTimeOffset CreatedAt { get; set; }
}
