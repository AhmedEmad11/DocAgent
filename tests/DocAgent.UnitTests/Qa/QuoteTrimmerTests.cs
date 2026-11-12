using DocAgent.Infrastructure.Qa;

namespace DocAgent.UnitTests.Qa;

public sealed class QuoteTrimmerTests
{
    [Fact]
    public void Short_chunk_is_returned_whole_with_collapsed_whitespace()
    {
        Assert.Equal("One line. Two lines.", QuoteTrimmer.Trim("One   line.\n\nTwo lines.", "anything", 300));
    }

    [Fact]
    public void Long_chunk_is_cut_to_the_most_relevant_sentence_within_the_limit()
    {
        var chunk = string.Join(' ', Enumerable.Repeat("This filler sentence says nothing useful at all.", 12))
            + " The monthly price of the Pro plan is 20 dollars. Billing happens on the first day. "
            + string.Join(' ', Enumerable.Repeat("More unrelated filler sentence goes here today.", 12));

        var quote = QuoteTrimmer.Trim(chunk, "What is the monthly price of the Pro plan?", 120);

        Assert.StartsWith("The monthly price of the Pro plan is 20 dollars.", quote);
        Assert.True(quote.Length <= 120);
    }

    [Fact]
    public void A_single_overlong_sentence_is_cut_at_a_word_with_an_ellipsis()
    {
        var chunk = string.Join(' ', Enumerable.Repeat("word", 200));

        var quote = QuoteTrimmer.Trim(chunk, "word", 50);

        Assert.True(quote.Length <= 50);
        Assert.EndsWith("…", quote);
    }

    [Fact]
    public void Arabic_sentences_are_split_on_the_arabic_question_mark_and_period()
    {
        var chunk = string.Join(' ', Enumerable.Repeat("هذه جملة حشو لا فائدة منها أبدا.", 15)) + " تكلفة الخطة الشهرية عشرون دولارا. " + string.Join(' ', Enumerable.Repeat("جملة أخرى لا علاقة لها بالموضوع.", 15));

        var quote = QuoteTrimmer.Trim(chunk, "كم تكلفة الخطة الشهرية؟", 100);

        Assert.StartsWith("تكلفة الخطة الشهرية عشرون دولارا.", quote);
    }
}
