using DocAgent.Core.Retrieval;
using DocAgent.Infrastructure.Agents;
using DocAgent.Infrastructure.Llm;
using DocAgent.Infrastructure.Qa;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DocAgent.UnitTests.Agents;

/// <summary>Argument validation of the tools; these paths never reach the database.</summary>
public sealed class ToolboxValidationTests
{
    private sealed class RecordingRetriever : IRetriever
    {
        public List<(string Query, Guid[]? Ids)> Calls { get; } = [];

        public Task<IReadOnlyList<RetrievedChunk>> SearchAsync(string query, IReadOnlyCollection<Guid>? documentIds, int topK, CancellationToken ct = default)
        {
            Calls.Add((query, documentIds?.ToArray()));
            return Task.FromResult<IReadOnlyList<RetrievedChunk>>([]);
        }
    }

    private static readonly AgentDocument Own = new(Guid.NewGuid(), "own.txt", 2);

    private static (AgentToolbox Toolbox, RecordingRetriever Retriever, SourceRegistry Registry) Create()
    {
        var retriever = new RecordingRetriever();
        var registry = new SourceRegistry();
        var options = new AgentOptions();
        var workflows = new DocumentWorkflows(null!, Options.Create(options), Options.Create(new QaOptions()), NullLogger<DocumentWorkflows>.Instance);
        var route = new LlmRoute(new StubChatClient((_, _, _) => StubChatClient.Text("x")), new LlmRouteInfo("development", "Test", "m"));
        var state = new AgentRunState(10, 3600, TimeSpan.FromMinutes(1));
        return (new AgentToolbox(null!, retriever, workflows, registry, state, options, route, TimeSpan.FromMinutes(2), [Own]), retriever, registry);
    }

    [Fact]
    public void The_tool_set_is_fixed_and_no_tool_takes_a_user_id()
    {
        var (toolbox, _, _) = Create();

        var tools = toolbox.CreateTools(NullLogger.Instance, null).OfType<AIFunction>().ToList();

        Assert.Equal(
            ["compare_documents", "get_chunk_context", "get_document_outline", "search_documents", "summarize_document"],
            tools.Select(t => t.Name).Order());
        var parameterNames = tools.SelectMany(t => t.JsonSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name)).ToList();
        Assert.DoesNotContain(parameterNames, n => n.Contains("user", StringComparison.OrdinalIgnoreCase));
        Assert.All(tools, t => Assert.False(string.IsNullOrWhiteSpace(t.Description)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Search_rejects_an_empty_query(string query)
    {
        var (toolbox, retriever, _) = Create();

        Assert.StartsWith("Error:", await toolbox.SearchDocumentsAsync(query));
        Assert.Empty(retriever.Calls);
    }

    [Fact]
    public async Task Search_rejects_a_query_over_500_characters()
    {
        var (toolbox, retriever, _) = Create();

        Assert.StartsWith("Error:", await toolbox.SearchDocumentsAsync(new string('a', 501)));
        Assert.Empty(retriever.Calls);
    }

    [Fact]
    public async Task Search_with_an_unknown_or_foreign_document_never_reaches_the_retriever()
    {
        var (toolbox, retriever, _) = Create();

        var foreign = await toolbox.SearchDocumentsAsync("price", [Guid.NewGuid().ToString()]);
        var byName = await toolbox.SearchDocumentsAsync("price", ["someone-elses.txt"]);
        var mixed = await toolbox.SearchDocumentsAsync("price", [Own.Id.ToString(), Guid.NewGuid().ToString()]);
        var tooMany = await toolbox.SearchDocumentsAsync("price", Enumerable.Repeat(Own.Id.ToString(), 21).ToArray());

        Assert.All([foreign, byName, mixed, tooMany], r => Assert.StartsWith("Error:", r));
        Assert.Empty(retriever.Calls);
    }

    [Fact]
    public async Task Search_defaults_to_all_own_documents_and_accepts_ids_and_exact_names()
    {
        var (toolbox, retriever, _) = Create();

        await toolbox.SearchDocumentsAsync("price");
        await toolbox.SearchDocumentsAsync("price list", [Own.Id.ToString()]);
        await toolbox.SearchDocumentsAsync("price table", ["OWN.txt"]);

        Assert.All(retriever.Calls, c => Assert.Equal([Own.Id], c.Ids!));
        Assert.Equal(3, retriever.Calls.Count);
    }

    [Fact]
    public async Task The_same_search_twice_is_not_repeated()
    {
        var (toolbox, retriever, _) = Create();

        await toolbox.SearchDocumentsAsync("Price");
        var second = await toolbox.SearchDocumentsAsync("price");

        Assert.Single(retriever.Calls);
        Assert.Contains("already ran this exact search", second);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(4, 0)]
    [InlineData(0, 99)]
    public async Task Chunk_context_validates_the_window(int before, int after)
    {
        var (toolbox, _, registry) = Create();
        registry.Register(SourceRegistryTests.Chunk());

        Assert.Contains("between 0 and 3", await toolbox.GetChunkContextAsync(1, before, after));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-3)]
    [InlineData(int.MaxValue)]
    public async Task Chunk_context_only_resolves_numbers_handed_out_in_this_run(int index)
    {
        var (toolbox, _, _) = Create(); // empty registry

        Assert.Contains($"no source with index {index}", await toolbox.GetChunkContextAsync(index, 1, 1));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-document")]
    public async Task Outline_and_summary_of_an_unknown_document_say_so(string reference)
    {
        var (toolbox, _, _) = Create();

        Assert.StartsWith("Error: no document", await toolbox.GetDocumentOutlineAsync(reference));
        Assert.StartsWith("Error: no document", await toolbox.SummarizeDocumentAsync(reference));
        Assert.StartsWith("Error: no document", await toolbox.CompareDocumentsAsync(reference, Own.FileName));
        Assert.StartsWith("Error: the two documents must be different", await toolbox.CompareDocumentsAsync(Own.FileName, Own.Id.ToString()));
    }
}
