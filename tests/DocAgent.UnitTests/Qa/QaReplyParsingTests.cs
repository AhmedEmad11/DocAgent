using DocAgent.Infrastructure.Qa;

namespace DocAgent.UnitTests.Qa;

public sealed class QaReplyParsingTests
{
    [Fact]
    public void Json_reply_is_read()
    {
        var (answer, notFound) = QaService.ParseReply("{\"answer\":\"It costs 20 dollars [1].\",\"notFound\":false}");

        Assert.Equal("It costs 20 dollars [1].", answer);
        Assert.False(notFound);
    }

    [Fact]
    public void Json_inside_a_code_fence_is_read()
    {
        var (answer, notFound) = QaService.ParseReply("```json\n{\"answer\":\"Not in the documents.\",\"notFound\":true}\n```");

        Assert.Equal("Not in the documents.", answer);
        Assert.True(notFound);
    }

    [Fact]
    public void Plain_text_falls_back_to_being_the_answer()
    {
        var (answer, notFound) = QaService.ParseReply("It costs 20 dollars [1].");

        Assert.Equal("It costs 20 dollars [1].", answer);
        Assert.False(notFound);
    }

    [Fact]
    public void Broken_json_falls_back_to_plain_text_and_empty_gives_null()
    {
        Assert.Equal("{\"answer\": oops}", QaService.ParseReply("{\"answer\": oops}").Answer);
        Assert.Null(QaService.ParseReply("  ").Answer);
        Assert.Null(QaService.ParseReply(null).Answer);
    }

    [Fact]
    public void Not_found_message_follows_the_language_of_the_question()
    {
        Assert.Equal(QaService.NotFoundMessageEnglish, QaService.NotFoundMessage("What is the price?"));
        Assert.Equal(QaService.NotFoundMessageArabic, QaService.NotFoundMessage("ما هو السعر؟"));
    }
}
