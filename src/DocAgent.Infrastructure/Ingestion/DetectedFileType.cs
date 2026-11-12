namespace DocAgent.Infrastructure.Ingestion;

public enum DetectedFileType { Pdf, Docx, PlainText, Markdown }

public static class DetectedFileTypeExtensions
{
    public const string PdfContentType = "application/pdf";
    public const string DocxContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    public const string TextContentType = "text/plain";
    public const string MarkdownContentType = "text/markdown";

    public static string ToContentType(this DetectedFileType type) => type switch
    {
        DetectedFileType.Pdf => PdfContentType,
        DetectedFileType.Docx => DocxContentType,
        DetectedFileType.Markdown => MarkdownContentType,
        _ => TextContentType,
    };

    public static DetectedFileType FromContentType(string contentType) => contentType switch
    {
        PdfContentType => DetectedFileType.Pdf,
        DocxContentType => DetectedFileType.Docx,
        MarkdownContentType => DetectedFileType.Markdown,
        TextContentType => DetectedFileType.PlainText,
        _ => throw new ArgumentOutOfRangeException(nameof(contentType), "Unknown stored content type."),
    };
}
