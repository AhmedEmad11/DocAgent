using DocAgent.Core.Retrieval;
using DocAgent.Infrastructure.Agents;
using DocAgent.Infrastructure.Qa;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DocAgent.UnitTests.Agents;

public sealed class TokenBatcherTests
{
    [Fact]
    public void Batches_keep_order_and_stay_within_the_budget()
    {
        var batches = TokenBatcher.Group(new[] { 300, 300, 300, 300, 300, 300, 300 }, x => x, 1000);

        Assert.Equal([[300, 300, 300], [300, 300, 300], [300]], batches);
    }

    [Fact]
    public void An_item_larger_than_the_budget_gets_its_own_batch_and_none_is_empty()
    {
        var batches = TokenBatcher.Group(new[] { 100, 5000, 100 }, x => x, 1000);

        Assert.Equal([[100], [5000], [100]], batches);
        Assert.All(batches, b => Assert.NotEmpty(b));
        Assert.Empty(TokenBatcher.Group(Array.Empty<int>(), x => x, 1000));
    }

    [Fact]
    public async Task Map_reduce_sizes_every_model_call_under_the_budget_and_merges_the_partial_summaries()
    {
        var options = new AgentOptions { MapBudgetTokens = 1000 };
        var chunks = Enumerable.Range(0, 10)
            .Select(i => new RetrievedChunk(Guid.NewGuid(), Guid.NewGuid(), "doc.txt", i + 1, null, i, $"Paragraph {i} " + new string('x', 20), 400, 0))
            .ToList();
        var registry = new SourceRegistry();
        var reduceCalls = 0;
        var stub = new StubChatClient((_, messages, _) =>
        {
            var system = messages[0].Text;
            var user = messages[1].Text;
            if (system.Contains("You merge partial summaries"))
            {
                reduceCalls++;
                return StubChatClient.Text("- merged point [1][99]"); // 99 is not a source: must be removed
            }

            var first = System.Text.RegularExpressions.Regex.Match(user, "<source index=\"(\\d+)\"").Groups[1].Value;
            return StubChatClient.Text($"- point [{first}]");
        });
        var workflows = new DocumentWorkflows(null!, Options.Create(options), Options.Create(new QaOptions()), NullLogger<DocumentWorkflows>.Instance);
        var route = new DocAgent.Infrastructure.Llm.LlmRoute(stub, new DocAgent.Infrastructure.Llm.LlmRouteInfo("development", "Test", "m"));
        var partials = new List<string>();

        var text = await workflows.MapReduceAsync(chunks, null, "English", topics: false, registry, route, partials, CancellationToken.None);

        // 10 chunks of 400 tokens, budget 1000: two chunks per map call (5 map calls), then the 5 partials are merged.
        Assert.Equal(5, partials.Count);
        var mapCalls = stub.Calls.Where(c => !c.Messages[0].Text.Contains("You merge partial summaries")).ToList();
        Assert.Equal(5, mapCalls.Count);
        Assert.All(mapCalls, c => Assert.True(System.Text.RegularExpressions.Regex.Matches(c.Messages[1].Text, "<source index").Count <= 2));
        Assert.True(reduceCalls >= 1);
        Assert.Equal("- merged point [1]", text);
        Assert.Equal(10, registry.Count);
    }

    [Fact]
    public async Task A_small_document_is_summarized_in_a_single_call()
    {
        var chunks = new[] { new RetrievedChunk(Guid.NewGuid(), Guid.NewGuid(), "doc.txt", 1, null, 0, "Short.", 20, 0) };
        var stub = new StubChatClient((_, _, _) => StubChatClient.Text("- short [1]"));
        var workflows = new DocumentWorkflows(null!, Options.Create(new AgentOptions()), Options.Create(new QaOptions()), NullLogger<DocumentWorkflows>.Instance);
        var route = new DocAgent.Infrastructure.Llm.LlmRoute(stub, new DocAgent.Infrastructure.Llm.LlmRouteInfo("development", "Test", "m"));

        var text = await workflows.MapReduceAsync(chunks, "focus words", "English", false, new SourceRegistry(), route, [], CancellationToken.None);

        Assert.Equal("- short [1]", text);
        Assert.Single(stub.Calls);
        Assert.Contains("focus words", stub.Calls[0].Messages[0].Text);
    }

    [Theory]
    [InlineData("سياسة الإجازات في الشركة وأنواعها", "Arabic")]
    [InlineData("Employee handbook", "English")]
    public void Language_follows_the_document_text(string text, string expected)
    {
        var chunk = new RetrievedChunk(Guid.NewGuid(), Guid.NewGuid(), "f", 1, null, 0, text, 10, 0);

        Assert.Equal(expected, DocumentWorkflows.Language(null, [chunk]));
        Assert.Equal("English", DocumentWorkflows.Language("price", [chunk]));
        Assert.Equal("Arabic", DocumentWorkflows.Language("الأسعار", [chunk]));
    }
}
