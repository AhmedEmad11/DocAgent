using DocAgent.Core.Retrieval;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using DocAgent.Infrastructure.Llm;
using DocAgent.Infrastructure.Qa;
using DocAgent.Infrastructure.Retrieval;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DocAgent.UnitTests.Qa;

public sealed class OllamaRouteTests
{
    private sealed class OneChunkRetriever : IRetriever
    {
        public Task<IReadOnlyList<RetrievedChunk>> SearchAsync(string query, IReadOnlyCollection<Guid>? documentIds, int topK, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RetrievedChunk>>([new RetrievedChunk(Guid.NewGuid(), Guid.NewGuid(), "a.txt", null, null, 0, "text", 1, 0.9)]);
    }

    private sealed class Direct(OllamaChatClientFactory ollama) : ILlmClientFactory
    {
        public ValueTask<LlmRoute> GetRouteAsync(CancellationToken ct = default) => ollama.GetRouteAsync(ct);
    }

    [Fact]
    public async Task Route_describes_the_development_ollama_model_from_configuration()
    {
        using var factory = new OllamaChatClientFactory(
            Options.Create(new LlmOptions { Ollama = { Model = "my-model" } }), NullLoggerFactory.Instance);

        var route = await factory.GetRouteAsync();

        Assert.Equal(new LlmRouteInfo("development", "Ollama", "my-model"), route.Info);
    }

    [Fact]
    public async Task Connection_failure_becomes_llm_unavailable()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop(); // nothing listens on this port any more: connections are refused
        using var factory = new OllamaChatClientFactory(
            Options.Create(new LlmOptions { Ollama = { Endpoint = $"http://127.0.0.1:{port}" } }),
            NullLoggerFactory.Instance);
        var qa = new QaService(new OneChunkRetriever(), new Direct(factory), Options.Create(new RetrievalOptions()), Options.Create(new QaOptions()), NullLogger<QaService>.Instance);

        var ex = await Assert.ThrowsAsync<LlmUnavailableException>(() => qa.AskAsync("question?", null, CancellationToken.None));

        Assert.Equal("The language model is unavailable.", ex.Message);
    }

    [Fact]
    public void Unknown_provider_fails_fast_at_startup()
    {
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Llm:Provider"] = "Nope" }).Build();

        Assert.Throws<InvalidOperationException>(() => { new ServiceCollection().AddLlm(config, allowDevelopmentRoute: true); });
    }
}
