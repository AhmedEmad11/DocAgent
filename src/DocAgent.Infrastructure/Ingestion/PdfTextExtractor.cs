using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace DocAgent.Infrastructure.Ingestion;

public static class PdfTextExtractor
{
    public static ExtractedDocument Extract(byte[] content, IngestionOptions options, CancellationToken ct)
    {
        try
        {
            using var pdf = PdfDocument.Open(content, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });
            var pageCount = pdf.NumberOfPages;
            if (pageCount > options.MaxPdfPages)
            {
                throw new DocumentParseException($"The PDF has {pageCount} pages; the maximum is {options.MaxPdfPages}.");
            }

            var segments = new List<TextSegment>();
            long meaningfulChars = 0;
            for (var number = 1; number <= pageCount; number++)
            {
                ct.ThrowIfCancellationRequested();
                var page = pdf.GetPage(number);
                var text = TextCleaner.Clean(ArabicTextFixer.Fix(ContentOrderTextExtractor.GetText(page, addDoubleNewline: true))).Trim();
                meaningfulChars += text.Count(c => !char.IsWhiteSpace(c));
                if (text.Length > 0)
                {
                    segments.Add(new TextSegment(text, PageNumber: number));
                }
            }

            if (pageCount == 0 || meaningfulChars == 0 || (double)meaningfulChars / pageCount < options.MinCharsPerPdfPage)
            {
                throw new NoExtractableTextException(NoExtractableTextException.ScannedPdfReason);
            }

            return new ExtractedDocument(segments, pageCount);
        }
        catch (Exception ex) when (ex is not DocumentParseException and not OperationCanceledException)
        {
            // PdfPig throws many exception types for malformed, encrypted or truncated files.
            throw new DocumentParseException("The PDF could not be read (it may be corrupt or password-protected).", ex);
        }
    }
}
