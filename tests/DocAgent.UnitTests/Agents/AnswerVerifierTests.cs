using DocAgent.Core.Retrieval;
using DocAgent.Infrastructure.Agents;
using DocAgent.Infrastructure.Llm;
using DocAgent.Infrastructure.Qa;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DocAgent.UnitTests.Agents;

public sealed class AnswerVerifierTests
{
    private static readonly AnswerVerifier Verifier = new(Options.Create(new QaOptions()), NullLogger<AnswerVerifier>.Instance);

    private static SourceRegistry Registry(params string[] texts)
    {
        var registry = new SourceRegistry();
        foreach (var text in texts)
        {
            registry.Register(new RetrievedChunk(Guid.NewGuid(), Guid.NewGuid(), "f.txt", 1, null, 0, text, 20, 0));
        }

        return registry;
    }

    private static LlmRoute Route(StubChatClient client) => new(client, new LlmRouteInfo("development", "Test", "m"));

    [Fact]
    public void Sentences_are_split_and_a_marker_after_the_full_stop_stays_with_its_sentence()
    {
        var sentences = AnswerVerifier.SplitSentences("The Pro plan costs 20 dollars. [1] The Basic plan costs 10 dollars [2]. Version 3.5 is current [1].\n- A bullet [2]");

        Assert.Equal(
            ["The Pro plan costs 20 dollars [1].", "The Basic plan costs 10 dollars [2].", "Version 3.5 is current [1].", "- A bullet [2]"],
            sentences);
    }

    [Fact]
    public async Task Unsupported_claims_are_removed_and_the_rest_is_kept()
    {
        var registry = Registry("The Pro plan costs 20 dollars.", "Support answers in 4 hours.");
        var stub = new StubChatClient((_, _, _) => StubChatClient.Text(
            """{"claims":[{"id":1,"supported":true,"source":1},{"id":2,"supported":false,"source":0},{"id":3,"supported":true,"source":2}]}"""));

        var result = await Verifier.VerifyAsync("Q?", "Pro costs 20 dollars [1]. It includes a free phone [1]. Support takes 4 hours [2].", registry, Route(stub), default);

        Assert.Equal("Pro costs 20 dollars [1].\nSupport takes 4 hours [2].", result.Answer);
        Assert.Equal((3, 1, false), (result.Claims, result.Removed, result.Skipped));
        Assert.NotNull(stub.Calls[0].Options!.ResponseFormat); // strict JSON schema, not free text
        Assert.Contains("The Pro plan costs 20 dollars.", stub.Calls[0].Messages[1].Text);
        Assert.Contains("untrusted", stub.Calls[0].Messages[0].Text);
    }

    [Fact]
    public async Task When_nothing_is_supported_there_is_no_answer()
    {
        var registry = Registry("Something unrelated.");
        var stub = new StubChatClient((_, _, _) => StubChatClient.Text("""{"claims":[{"id":1,"supported":false,"source":0}]}"""));

        var result = await Verifier.VerifyAsync("Q?", "The moon is cheese [1].", registry, Route(stub), default);

        Assert.Null(result.Answer);
        Assert.Equal(1, result.Removed);
    }

    [Fact]
    public async Task A_claim_the_verifier_does_not_mention_is_not_kept()
    {
        var registry = Registry("A.", "B.");
        var stub = new StubChatClient((_, _, _) => StubChatClient.Text("""{"claims":[{"id":1,"supported":true,"source":1}]}"""));

        var result = await Verifier.VerifyAsync("Q?", "A is true [1]. B is true [2].", registry, Route(stub), default);

        Assert.Equal("A is true [1].", result.Answer);
    }

    [Fact]
    public async Task A_supporting_source_other_than_the_cited_one_is_added_to_the_sentence()
    {
        var registry = Registry("First.", "Second.");
        var stub = new StubChatClient((_, _, _) => StubChatClient.Text("""{"claims":[{"id":1,"supported":true,"source":2}]}"""));

        var result = await Verifier.VerifyAsync("Q?", "The fact [1].", registry, Route(stub), default);

        Assert.Equal("The fact [1]. [2]", result.Answer);
    }

    [Fact]
    public async Task An_answer_without_any_citation_cannot_be_shown_to_be_supported()
    {
        var stub = new StubChatClient((_, _, _) => throw new InvalidOperationException("must not be called"));

        var result = await Verifier.VerifyAsync("Q?", "Uncited prose.", Registry("x"), Route(stub), default);

        Assert.Null(result.Answer);
        Assert.Empty(stub.Calls);
    }

    [Fact]
    public async Task Unusable_replies_are_retried_once_then_the_draft_is_kept_unchecked()
    {
        var registry = Registry("Fact.");
        var garbage = new StubChatClient((_, _, _) => StubChatClient.Text("sure, looks fine"));
        var skipped = await Verifier.VerifyAsync("Q?", "Fact [1].", registry, Route(garbage), default);

        Assert.Equal(2, garbage.Calls.Count);
        Assert.True(skipped.Skipped);
        Assert.Equal("Fact [1].", skipped.Answer);

        var flaky = new StubChatClient((n, _, _) => StubChatClient.Text(n == 0 ? "{" : """{"claims":[{"id":1,"supported":false,"source":0}]}"""));
        var second = await Verifier.VerifyAsync("Q?", "Fact [1].", registry, Route(flaky), default);

        Assert.False(second.Skipped);
        Assert.Null(second.Answer);
    }

    [Fact]
    public async Task An_answer_that_only_says_the_information_is_missing_is_rejected_even_when_its_claims_are_supported()
    {
        var registry = Registry("A medical report is required after three days of absence.");
        var stub = new StubChatClient((_, _, _) => StubChatClient.Text(
            """{"claims":[{"id":1,"supported":true,"source":1}],"answersQuestion":false}"""));

        var result = await Verifier.VerifyAsync(
            "What is the maximum sick leave per year?", "The documents do not say. A medical report is required after three days [1].", registry, Route(stub), default);

        Assert.Null(result.Answer);
        Assert.False(result.AnswersQuestion);
        Assert.Contains("Question: What is the maximum sick leave per year?", stub.Calls[0].Messages[1].Text);
        Assert.Contains("answersQuestion", stub.Calls[0].Messages[0].Text);
    }

    [Theory]
    [InlineData("""{"claims":[{"id":2,"supported":true,"source":1}]}""", true)]
    [InlineData("""prefix {"claims":[{"id":2,"supported":true,"source":1}]} suffix""", true)]
    [InlineData("""{"claims":[]}""", false)]
    [InlineData("not json", false)]
    [InlineData("", false)]
    public void Verdict_parsing(string json, bool usable)
    {
        var parsed = AnswerVerifier.ParseVerdictsForTest(json);

        Assert.Equal(usable, parsed is not null);
        if (usable)
        {
            Assert.Equal((true, 1), parsed!.Value.Verdicts[2]);
            Assert.True(parsed.Value.AnswersQuestion); // a missing flag does not reject the draft
        }
    }
}
