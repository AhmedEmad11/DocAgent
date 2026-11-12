using System.IO.Compression;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace DocAgent.Infrastructure.Ingestion;

/// <summary>Word body text per heading section. Word stores text in logical order, so Arabic needs no repair.</summary>
public static class DocxTextExtractor
{
    public static ExtractedDocument Extract(byte[] content, IngestionOptions options, CancellationToken ct)
    {
        try
        {
            EnsureSafeArchive(content, options);
            using var stream = new MemoryStream(content, writable: false);
            using var package = WordprocessingDocument.Open(stream, isEditable: false);
            var body = package.MainDocumentPart?.Document?.Body
                ?? throw new DocumentParseException("The Word document has no body.");
            var styleNames = LoadStyleNames(package);

            var segments = new List<TextSegment>();
            var current = new StringBuilder();
            string? section = null;

            void Flush()
            {
                var text = current.ToString().Trim();
                if (text.Length > 0)
                {
                    segments.Add(new TextSegment(text, Section: section));
                }

                current.Clear();
            }

            foreach (var paragraph in body.Descendants<Paragraph>())
            {
                ct.ThrowIfCancellationRequested();
                var text = TextCleaner.Clean(ParagraphText(paragraph)).Trim();
                if (text.Length == 0)
                {
                    continue;
                }

                if (IsHeading(paragraph, styleNames))
                {
                    Flush();
                    section = text.Length <= 200 ? text : text[..200];
                }

                current.Append(text).Append("\n\n");
            }

            Flush();
            return new ExtractedDocument(segments);
        }
        catch (Exception ex) when (ex is not DocumentParseException and not OperationCanceledException)
        {
            throw new DocumentParseException("The Word document could not be read (it may be corrupt or password-protected).", ex);
        }
    }

    /// <summary>Re-checks the zip limits (SEC-5); parsing must not be reachable with a file that skipped the upload checks.</summary>
    private static void EnsureSafeArchive(byte[] content, IngestionOptions options)
    {
        using var stream = new MemoryStream(content, writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        if (archive.Entries.Count > options.MaxDocxEntries || archive.Entries.Sum(e => e.Length) > options.MaxDocxUncompressedBytes)
        {
            throw new DocumentParseException("The Word document is too large when unpacked.");
        }
    }

    private static Dictionary<string, string> LoadStyleNames(WordprocessingDocument package)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var styles = package.MainDocumentPart?.StyleDefinitionsPart?.Styles;
        if (styles is null)
        {
            return names;
        }

        foreach (var style in styles.Elements<Style>())
        {
            if (style.StyleId?.Value is { } id && style.StyleName?.Val?.Value is { } name)
            {
                names[id] = name;
            }
        }

        return names;
    }

    private static bool IsHeading(Paragraph paragraph, Dictionary<string, string> styleNames)
    {
        var properties = paragraph.ParagraphProperties;
        if (properties?.OutlineLevel?.Val?.Value is { } level && level < 9)
        {
            return true;
        }

        var styleId = properties?.ParagraphStyleId?.Val?.Value;
        if (styleId is null)
        {
            return false;
        }

        var name = styleNames.TryGetValue(styleId, out var n) ? n : styleId;
        return name.StartsWith("heading", StringComparison.OrdinalIgnoreCase)
            || name.Equals("title", StringComparison.OrdinalIgnoreCase)
            || name.Equals("subtitle", StringComparison.OrdinalIgnoreCase);
    }

    private static string ParagraphText(Paragraph paragraph)
    {
        var sb = new StringBuilder();
        foreach (var element in paragraph.Descendants())
        {
            switch (element)
            {
                case Text t:
                    sb.Append(t.Text);
                    break;
                case TabChar:
                    sb.Append('\t');
                    break;
                case Break or CarriageReturn:
                    sb.Append('\n');
                    break;
            }
        }

        return sb.ToString();
    }
}
