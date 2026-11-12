using DocAgent.Infrastructure.Ingestion;
using DocAgent.TestSupport;
using System.Text;

namespace DocAgent.UnitTests.Ingestion;

public sealed class ExtractionTests
{
    private static readonly IngestionOptions Options = new();

    private static ExtractedDocument Extract(DetectedFileType type, byte[] content) =>
        DocumentTextExtractor.Extract(type, content, Options, CancellationToken.None);

    [Fact]
    public void Pdf_text_is_extracted_per_page_with_page_numbers()
    {
        var pdf = TestFiles.EnglishPdf(
            "Page one talks about pricing and the monthly plan. It has plenty of ordinary words in it.",
            "Page two talks about storage limits and support hours. It also has plenty of ordinary words.");

        var result = Extract(DetectedFileType.Pdf, pdf);

        Assert.Equal(2, result.PageCount);
        Assert.Equal([1, 2], result.Segments.Select(s => s.PageNumber!.Value));
        Assert.Contains("pricing", result.Segments[0].Text);
        Assert.Contains("storage limits", result.Segments[1].Text);
    }

    [Fact]
    public void Pdf_without_text_is_reported_as_scanned()
    {
        var ex = Assert.Throws<NoExtractableTextException>(() => Extract(DetectedFileType.Pdf, TestFiles.BlankPdf()));
        Assert.Equal("No extractable text: scanned PDFs are not supported.", ex.Message);
    }

    [Fact]
    public void Pdf_with_almost_no_text_per_page_is_reported_as_scanned()
    {
        // 3 pages, 25 non-space characters in total: well below 20 per page on average.
        var pdf = TestFiles.EnglishPdf("Page 1", "Page 2", "Page 3 fine");
        Assert.Throws<NoExtractableTextException>(() => Extract(DetectedFileType.Pdf, pdf));
    }

    [Fact]
    public void Corrupt_pdf_is_a_parse_error()
    {
        var bytes = Encoding.ASCII.GetBytes("%PDF-1.7\nthis is not really a pdf file at all");
        Assert.IsType<DocumentParseException>(Assert.ThrowsAny<DocumentParseException>(() => Extract(DetectedFileType.Pdf, bytes)));
    }

    [Fact]
    public void Docx_text_is_split_into_sections_by_heading()
    {
        var docx = TestFiles.Docx(["Intro before any heading.", "# Pricing", "The plan costs 20 dollars.", "## Details", "Billed monthly.", "# Support", "Email us."]);

        var result = Extract(DetectedFileType.Docx, docx);

        Assert.Equal([null, "Pricing", "Details", "Support"], result.Segments.Select(s => s.Section));
        Assert.Contains("Intro before any heading.", result.Segments[0].Text);
        Assert.Contains("The plan costs 20 dollars.", result.Segments[1].Text);
        Assert.Contains("Billed monthly.", result.Segments[2].Text);
        Assert.All(result.Segments, s => Assert.Null(s.PageNumber));
    }

    [Fact]
    public void Arabic_docx_keeps_logical_order()
    {
        var docx = TestFiles.Docx([$"# {TestFiles.ArabicPage1}", TestFiles.ArabicSentence1, TestFiles.ArabicSentence2], rtl: true);

        var result = Extract(DetectedFileType.Docx, docx);

        var segment = Assert.Single(result.Segments);
        Assert.Equal(TestFiles.ArabicPage1, segment.Section);
        Assert.Contains(TestFiles.ArabicSentence1, segment.Text);
        Assert.Contains(TestFiles.ArabicSentence2, segment.Text);
    }

    [Fact]
    public void Markdown_headings_become_sections_and_markup_is_removed()
    {
        const string md = "Preface text.\n\n# Pricing\n\nThe **plan** costs [20 dollars](http://example.com).\n\n- first item\n- second item\n\n## Details\n\n```\ncode line\n```\n";

        var result = Extract(DetectedFileType.Markdown, Encoding.UTF8.GetBytes(md));

        Assert.Equal([null, "Pricing", "Details"], result.Segments.Select(s => s.Section));
        Assert.Contains("The plan costs 20 dollars.", result.Segments[1].Text);
        Assert.Contains("first item", result.Segments[1].Text);
        Assert.DoesNotContain("**", result.Segments[1].Text);
        Assert.DoesNotContain("http://example.com", result.Segments[1].Text);
        Assert.Contains("code line", result.Segments[2].Text);
    }

    [Fact]
    public void Arabic_markdown_keeps_logical_order()
    {
        var md = $"# {TestFiles.ArabicPage1}\n\n{TestFiles.ArabicSentence1}\n\n## {TestFiles.ArabicSentence2}\n\nنص آخر.\n";

        var result = Extract(DetectedFileType.Markdown, Encoding.UTF8.GetBytes(md));

        Assert.Equal([TestFiles.ArabicPage1, TestFiles.ArabicSentence2], result.Segments.Select(s => s.Section));
        Assert.Contains(TestFiles.ArabicSentence1, result.Segments[0].Text);
    }

    [Fact]
    public void Plain_text_is_one_segment_and_a_bom_is_dropped()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("Hello\r\nworld")).ToArray();

        var result = Extract(DetectedFileType.PlainText, bytes);

        Assert.Equal("Hello\nworld", Assert.Single(result.Segments).Text);
    }

    [Fact]
    public void Empty_text_has_no_extractable_text()
    {
        Assert.Throws<NoExtractableTextException>(() => Extract(DetectedFileType.PlainText, Encoding.UTF8.GetBytes("  \n\n ")));
    }

    [Fact]
    public void Docx_with_too_many_entries_is_refused()
    {
        var tight = new IngestionOptions { MaxDocxEntries = 2 };
        Assert.Throws<DocumentParseException>(() =>
            DocumentTextExtractor.Extract(DetectedFileType.Docx, TestFiles.Docx(["Hello there"]), tight, CancellationToken.None));
    }

    [Fact]
    public void Text_cleaner_removes_nul_and_control_characters()
    {
        Assert.Equal("a b\nc\td", TextCleaner.Clean("a\0 b\r\nc\td\u0007"));
        Assert.Equal("x�y", TextCleaner.Clean("x\uD800y"));
    }
}
