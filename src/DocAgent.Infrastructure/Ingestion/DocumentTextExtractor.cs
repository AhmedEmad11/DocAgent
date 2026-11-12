namespace DocAgent.Infrastructure.Ingestion;

public static class DocumentTextExtractor
{
    public static ExtractedDocument Extract(DetectedFileType type, byte[] content, IngestionOptions options, CancellationToken ct)
    {
        var extracted = type switch
        {
            DetectedFileType.Pdf => PdfTextExtractor.Extract(content, options, ct),
            DetectedFileType.Docx => DocxTextExtractor.Extract(content, options, ct),
            DetectedFileType.Markdown => MarkdownTextExtractor.Extract(content, ct),
            _ => PlainTextExtractor.Extract(content),
        };

        if (extracted.Segments.Count == 0)
        {
            throw new NoExtractableTextException("The document contains no text.");
        }

        return extracted;
    }
}
