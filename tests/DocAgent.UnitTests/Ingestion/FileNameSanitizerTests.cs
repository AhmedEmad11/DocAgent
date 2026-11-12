using DocAgent.Infrastructure.Ingestion;

namespace DocAgent.UnitTests.Ingestion;

public sealed class FileNameSanitizerTests
{
    [Theory]
    [InlineData("report.pdf", "report.pdf")]
    [InlineData("C:\\Users\\bob\\report.pdf", "report.pdf")]
    [InlineData("../../etc/passwd.txt", "passwd.txt")]
    [InlineData("/tmp/x/notes.md", "notes.md")]
    [InlineData("a\0b\u0007c.txt", "abc.txt")]
    [InlineData("tab\tand\nnewline.txt", "tab and newline.txt")]
    [InlineData("  spaced   out  .txt", "spaced out .txt")]
    [InlineData("evil\u202Efdp.exe", "evilfdp.exe")]
    [InlineData("we<ird>:na|me?*.txt", "weirdname.txt")]
    [InlineData("تقرير.pdf", "تقرير.pdf")]
    [InlineData("..", "document")]
    [InlineData("", "document")]
    [InlineData(null, "document")]
    [InlineData("   ", "document")]
    public void Sanitizes(string? raw, string expected) => Assert.Equal(expected, FileNameSanitizer.Sanitize(raw));

    [Fact]
    public void Long_names_are_shortened_but_keep_the_extension()
    {
        var result = FileNameSanitizer.Sanitize(new string('a', 1000) + ".pdf");
        Assert.Equal(FileNameSanitizer.MaxLength, result.Length);
        Assert.EndsWith(".pdf", result);
    }

    [Fact]
    public void Long_names_do_not_split_surrogate_pairs()
    {
        var result = FileNameSanitizer.Sanitize(string.Concat(Enumerable.Repeat("😀", 300)) + ".txt");
        Assert.True(result.Length <= FileNameSanitizer.MaxLength);
        Assert.DoesNotContain(result.Where(char.IsSurrogate).Chunk(2), pair => pair.Length != 2);
    }
}
