using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaSharp;
using OllamaSharp.Models;
using OllamaSharp.Models.Chat;

namespace DocAgent.Infrastructure.Llm;

/// <summary>Development route: a local Ollama server through OllamaSharp's <see cref="IChatClient"/>.</summary>
public sealed class OllamaChatClientFactory : ILlmClientFactory, IDisposable
{
    private readonly HttpClient _http;
    private readonly LlmRoute _route;

    public OllamaChatClientFactory(IOptions<LlmOptions> options, ILoggerFactory loggerFactory)
    {
        var ollama = options.Value.Ollama;
        // A plain HttpClient on purpose: the shared HTTP resilience handler has short attempt timeouts that suit
        // normal APIs, not a local model that may take a minute. The Q&A request timeout bounds the call instead.
        _http = new HttpClient { BaseAddress = new Uri(ollama.Endpoint), Timeout = Timeout.InfiniteTimeSpan };
        var client = new OllamaApiClient(_http, ollama.Model);
        _route = new LlmRoute(
            ChatClientPipeline.Wrap(client, loggerFactory),
            new LlmRouteInfo("development", "Ollama", ollama.Model),
            new ChatOptions
            {
                RawRepresentationFactory = _ => new ChatRequest
                {
                    Think = ollama.Think,
                    Options = new RequestOptions { NumCtx = ollama.NumCtx },
                },
            });
    }

    public ValueTask<LlmRoute> GetRouteAsync(CancellationToken ct = default) => ValueTask.FromResult(_route);

    public void Dispose() => _http.Dispose();
}
