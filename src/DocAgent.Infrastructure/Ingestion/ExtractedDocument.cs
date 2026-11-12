namespace DocAgent.Infrastructure.Ingestion;

/// <summary>A run of text that shares source metadata: one PDF page, or one heading section of a docx/md file.</summary>
public sealed record TextSegment(string Text, int? PageNumber = null, string? Section = null);

public sealed record ExtractedDocument(IReadOnlyList<TextSegment> Segments, int? PageCount = null);

/// <summary>The file cannot be processed and retrying will not help. The message is shown to the user, so it never contains file content.</summary>
public class DocumentParseException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class NoExtractableTextException(string message) : DocumentParseException(message)
{
    public const string ScannedPdfReason = "No extractable text: scanned PDFs are not supported.";
}
