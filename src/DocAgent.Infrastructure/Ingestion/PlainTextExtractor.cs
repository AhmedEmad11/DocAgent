namespace DocAgent.Infrastructure.Ingestion;

public static class PlainTextExtractor
{
    public static ExtractedDocument Extract(byte[] content)
    {
        var text = TextCleaner.Clean(FileTypeSniffer.DecodeUtf8(content)).Trim();
        return new ExtractedDocument(text.Length == 0 ? [] : [new TextSegment(text)]);
    }
}
