using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace DocAgent.TestSupport;

/// <summary>Builds small PDF / DOCX / text files in memory for the tests.</summary>
public static class TestFiles
{
    public const string ArabicPage1 = "خطة الاشتراك";
    public const string ArabicSentence1 = "تكلفة الخطة الشهرية عشرون دولارا.";
    public const string ArabicSentence2 = "عاصمة فرنسا هي باريس.";

    public static string FixturePath(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "Fixtures", name);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            var sibling = Path.Combine(dir.FullName, "Fixtures", name);
            if (File.Exists(sibling))
            {
                return sibling;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Test fixture '{name}' not found.");
    }

    public static byte[] ReadFixture(string name) => File.ReadAllBytes(FixturePath(name));

    /// <summary>An English text PDF, one page per entry; lines are wrapped by word count.</summary>
    public static byte[] EnglishPdf(params string[] pages)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var text in pages)
        {
            var page = builder.AddPage(595, 842);
            var y = 800d;
            foreach (var line in WrapWords(text.Replace('\n', ' '), 12))
            {
                if (y < 40)
                {
                    page = builder.AddPage(595, 842); // long text flows onto extra pages
                    y = 800d;
                }

                page.AddText(line, 11, new PdfPoint(40, y), font);
                y -= 14;
            }
        }

        return builder.Build();
    }

    /// <summary>
    /// An Arabic PDF with the text stored in logical order (a producer that does no shaping or reordering),
    /// using the embedded Noto Naskh Arabic font.
    /// </summary>
    public static byte[] ArabicLogicalPdf(params string[] pages)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddTrueTypeFont(ReadFixture("NotoNaskhArabic-Regular.ttf"));
        foreach (var text in pages)
        {
            var page = builder.AddPage(595, 842);
            var y = 800d;
            foreach (var line in WrapWords(text, 7))
            {
                if (y < 40)
                {
                    page = builder.AddPage(595, 842);
                    y = 800d;
                }

                page.AddText(line, 14, new PdfPoint(300, y), font);
                y -= 24;
            }
        }

        return builder.Build();
    }

    /// <summary>A PDF whose pages draw no text at all (what a scanned document looks like to a text extractor).</summary>
    public static byte[] BlankPdf(int pages = 2)
    {
        var builder = new PdfDocumentBuilder();
        for (var i = 0; i < pages; i++)
        {
            builder.AddPage(595, 842);
        }

        return builder.Build();
    }

    /// <summary>A .docx; entries starting with "# " become Heading 1 paragraphs, "## " Heading 2.</summary>
    public static byte[] Docx(IEnumerable<string> paragraphs, bool rtl = false)
    {
        using var stream = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            var stylesPart = main.AddNewPart<StyleDefinitionsPart>();
            stylesPart.Styles = new Styles(
                HeadingStyle("Heading1", "heading 1"),
                HeadingStyle("Heading2", "heading 2"));

            var body = new Body();
            foreach (var raw in paragraphs)
            {
                var (style, text) = raw switch
                {
                    _ when raw.StartsWith("## ") => ("Heading2", raw[3..]),
                    _ when raw.StartsWith("# ") => ("Heading1", raw[2..]),
                    _ => (null, raw),
                };

                var properties = new ParagraphProperties();
                if (style is not null)
                {
                    properties.Append(new ParagraphStyleId { Val = style });
                }

                var runProperties = new RunProperties();
                if (rtl)
                {
                    properties.Append(new BiDi());
                    runProperties.Append(new RightToLeftText());
                }

                body.Append(new Paragraph(properties, new Run(runProperties, new Text(text) { Space = SpaceProcessingModeValues.Preserve })));
            }

            main.Document = new DocumentFormat.OpenXml.Wordprocessing.Document(body);
            main.Document.Save();
        }

        return stream.ToArray();
    }

    private static Style HeadingStyle(string id, string name) =>
        new(new StyleName { Val = name }) { Type = StyleValues.Paragraph, StyleId = id };

    private static IEnumerable<string> WrapWords(string text, int wordsPerLine)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i += wordsPerLine)
        {
            yield return string.Join(' ', words.Skip(i).Take(wordsPerLine));
        }
    }

    /// <summary>Deterministic English prose of roughly the requested number of words, split into paragraphs.</summary>
    public static string Prose(int words, int paragraphWords = 120)
    {
        string[] vocabulary =
        [
            "the", "service", "stores", "documents", "for", "each", "user", "and", "answers", "questions", "about", "them",
            "with", "citations", "pricing", "plans", "billing", "monthly", "storage", "support", "agent", "search", "result",
            "quickly", "securely", "model", "tokens", "chunks", "pages", "sections",
        ];
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < words; i++)
        {
            sb.Append(vocabulary[(i * 7 + i / 5) % vocabulary.Length]);
            if (i % 11 == 10)
            {
                sb.Append('.');
            }

            if (i % paragraphWords == paragraphWords - 1)
            {
                sb.Append("\n\n");
            }
            else
            {
                sb.Append(' ');
            }
        }

        return sb.ToString().Trim();
    }
}
