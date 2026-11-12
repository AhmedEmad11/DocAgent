using DocAgent.Infrastructure.Retrieval;

namespace DocAgent.UnitTests.Qa;

public sealed class FullTextQueryTests
{
    [Fact]
    public void Stop_words_are_dropped_and_codes_are_kept_whole()
    {
        var terms = FullTextQuery.Build("What is invoice INV-2026-0417 for?");

        Assert.NotNull(terms);
        Assert.Equal("invoice inv-2026-0417", terms.All);
        Assert.Equal("inv-2026-0417", terms.Identifiers);
    }

    [Fact]
    public void Question_without_identifiers_has_no_identifier_query()
    {
        var terms = FullTextQuery.Build("Who is Dr. Halvorsen?");

        Assert.Equal("dr halvorsen", terms!.All);
        Assert.Null(terms.Identifiers);
    }

    [Fact]
    public void Arabic_questions_are_tokenized_and_filtered()
    {
        var terms = FullTextQuery.Build("ما هي الفاتورة رقم 55012؟");

        Assert.Equal("الفاتورة رقم 55012", terms!.All);
        Assert.Equal("55012", terms.Identifiers);
    }

    [Fact]
    public void Only_stop_words_gives_null()
    {
        Assert.Null(FullTextQuery.Build("what is the"));
        Assert.Null(FullTextQuery.Build("?!"));
    }

    [Fact]
    public void Search_operators_in_the_question_are_neutralised()
    {
        var terms = FullTextQuery.Build("-secret OR \"quoted phrase\" & (x) | y");

        Assert.Equal("secret quoted phrase", terms!.All); // "x" and "y" are one character; operators never reach the query
    }
}
