namespace DocAgent.Core.Entities;

/// <summary>
/// Temporary staging row holding the uploaded bytes until extraction finishes (SRS D15: originals are
/// not kept). Deleted by the ingestion worker as soon as the document is Ready or Failed.
/// Not tenant-filtered: only the ingestion worker reads it, always by DocumentId.
/// </summary>
public class DocumentUpload
{
    public Guid DocumentId { get; set; }
    public required byte[] Content { get; set; }
}
