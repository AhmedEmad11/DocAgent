namespace DocAgent.Core.Entities;

public class IngestionJob
{
    public Guid Id { get; set; }
    public Guid DocumentId { get; set; }

    /// <summary>Denormalized owner so the tenant filter applies without a join.</summary>
    public Guid UserId { get; set; }
    public IngestionJobState State { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
