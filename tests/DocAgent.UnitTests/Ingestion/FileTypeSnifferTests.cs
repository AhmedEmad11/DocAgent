using System.IO.Compression;
using System.Text;
using DocAgent.Infrastructure.Ingestion;
using DocAgent.TestSupport;

namespace DocAgent.UnitTests.Ingestion;

public sealed class FileTypeSnifferTests
{
    private static FileSniffResult Detect(byte[] content, string name) => FileTypeSniffer.Detect(content, name);

    [Fact]
    public void Detects_a_real_pdf() =>
        Assert.Equal(DetectedFileType.Pdf, Detect(TestFiles.EnglishPdf("Hello there"), "a.pdf").Type);

    [Fact]
    public void Detects_a_real_docx() =>
        Assert.Equal(DetectedFileType.Docx, Detect(TestFiles.Docx(["Hello"]), "a.docx").Type);

    [Theory]
    [InlineData("a.txt", DetectedFileType.PlainText)]
    [InlineData("A.TXT", DetectedFileType.PlainText)]
    [InlineData("notes.md", DetectedFileType.Markdown)]
    [InlineData("notes.markdown", DetectedFileType.Markdown)]
    public void Text_type_follows_the_extension(string name, DetectedFileType expected) =>
        Assert.Equal(expected, Detect("# héllo مرحبا"u8.ToArray(), name).Type);

    [Fact]
    public void Utf8_bom_is_accepted() =>
        Assert.Equal(DetectedFileType.PlainText, Detect([.. Encoding.UTF8.GetPreamble(), .. "hi"u8.ToArray()], "a.txt").Type);

    [Theory]
    [InlineData("a.pdf")]
    [InlineData("a.exe")]
    [InlineData("a")]
    [InlineData("a.docx")]
    public void Unknown_extension_or_mismatch_is_rejected_for_text(string name)
    {
        var result = Detect("just some text"u8.ToArray(), name);
        Assert.False(result.IsSupported);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Pdf_content_with_a_txt_extension_is_a_mismatch()
    {
        var result = Detect(TestFiles.EnglishPdf("Hello there"), "report.txt");
        Assert.False(result.IsSupported);
        Assert.Contains("PDF", result.Error);
    }

    [Fact]
    public void Docx_content_with_a_pdf_extension_is_a_mismatch() =>
        Assert.False(Detect(TestFiles.Docx(["Hello"]), "a.pdf").IsSupported);

    [Fact]
    public void Binary_data_named_txt_is_rejected() =>
        Assert.False(Detect([0x00, 0x01, 0x02, 0xFF, 0xFE, 0x10], "a.txt").IsSupported);

    [Fact]
    public void Text_with_a_nul_byte_is_rejected() =>
        Assert.False(Detect("hello\0world"u8.ToArray(), "a.txt").IsSupported);

    [Fact]
    public void Invalid_utf8_is_rejected() =>
        Assert.False(Detect([0x68, 0x69, 0xC3, 0x28], "a.txt").IsSupported);

    [Fact]
    public void Empty_file_is_rejected()
    {
        var result = Detect([], "a.txt");
        Assert.False(result.IsSupported);
        Assert.Contains("empty", result.Error);
    }

    [Fact]
    public void Plain_zip_that_is_not_a_docx_is_rejected()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(zip.CreateEntry("hello.txt").Open());
            writer.Write("hi");
        }

        Assert.False(Detect(stream.ToArray(), "a.docx").IsSupported);
    }

    [Fact]
    public void Zip_bomb_is_rejected_by_uncompressed_size()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var w = new StreamWriter(zip.CreateEntry("[Content_Types].xml").Open())) { w.Write("<x/>"); }
            using (var entry = zip.CreateEntry("word/document.xml", CompressionLevel.SmallestSize).Open())
            {
                var zeros = new byte[1024 * 1024];
                for (var i = 0; i < 60; i++) { entry.Write(zeros); } // 60 MB of zeros compresses to ~60 KB
            }
        }

        var bytes = stream.ToArray();
        Assert.True(bytes.Length < 1024 * 1024);
        var result = FileTypeSniffer.Detect(bytes, "a.docx", maxDocxUncompressedBytes: 50L * 1024 * 1024);
        Assert.False(result.IsSupported);
        Assert.Contains("unreasonable size", result.Error);
    }

    [Fact]
    public void Zip_with_too_many_entries_is_rejected()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (var i = 0; i < 30; i++) { zip.CreateEntry($"f{i}.xml"); }
        }

        var result = FileTypeSniffer.Detect(stream.ToArray(), "a.docx", maxDocxEntries: 10);
        Assert.False(result.IsSupported);
    }

    [Fact]
    public void Truncated_zip_is_rejected() =>
        Assert.False(Detect([0x50, 0x4B, 0x03, 0x04, 0x01, 0x02, 0x03], "a.docx").IsSupported);
}
