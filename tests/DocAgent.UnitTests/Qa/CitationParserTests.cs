using DocAgent.Infrastructure.Qa;

namespace DocAgent.UnitTests.Qa;

public sealed class CitationParserTests
{
    [Fact]
    public void Valid_markers_are_returned_sorted_and_distinct()
    {
        var result = CitationParser.Parse("It costs 20 dollars [2]. Billing is monthly [1][2].", 3);

        Assert.Equal([1, 2], result.Indexes);
        Assert.Equal("It costs 20 dollars [2]. Billing is monthly [1][2].", result.Answer);
    }

    [Fact]
    public void Out_of_range_markers_are_dropped_from_list_and_text()
    {
        var result = CitationParser.Parse("Fact one [1]. Invented [7]. Zero [0]. Mixed [2, 9].", 2);

        Assert.Equal([1, 2], result.Indexes);
        Assert.Equal("Fact one [1]. Invented. Zero. Mixed [2].", result.Answer);
    }

    [Fact]
    public void Answer_without_markers_has_no_citations()
    {
        var result = CitationParser.Parse("No markers here, just text [not a citation].", 5);

        Assert.Empty(result.Indexes);
        Assert.Equal("No markers here, just text [not a citation].", result.Answer);
    }

    [Fact]
    public void Comma_lists_and_arabic_indic_digits_are_understood()
    {
        var result = CitationParser.Parse("الجواب [١] و [٢، ٣].", 3);

        Assert.Equal([1, 2, 3], result.Indexes);
    }

    [Fact]
    public void Huge_numbers_do_not_overflow()
    {
        var result = CitationParser.Parse("x [99999999999999999999] y [1]", 2);

        Assert.Equal([1], result.Indexes);
    }
}
