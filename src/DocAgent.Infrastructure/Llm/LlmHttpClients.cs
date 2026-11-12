using DocAgent.Core.Entities;

namespace DocAgent.Infrastructure.Llm;

/// <summary>
/// One long-lived HttpClient per provider, each bound to its fixed host by <see cref="ProviderHttpHandler"/>.
/// Created directly (not through IHttpClientFactory): the factory's default logging handler and the shared 30-second
/// resilience pipeline suit neither secrets nor LLM calls. Timeouts come from the caller's cancellation token.
/// </summary>
public sealed class LlmHttpClients : IDisposable
{
    private readonly Dictionary<LlmProvider, HttpClient> _clients = [];

    public LlmHttpClients() : this(_ => new SocketsHttpHandler
    {
        AllowAutoRedirect = false, // an Authorization header must never follow a redirect elsewhere
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    {
    }

    /// <summary>Tests pass a stub transport.</summary>
    public LlmHttpClients(Func<LlmProvider, HttpMessageHandler> transport)
    {
        foreach (var provider in LlmProviders.All)
        {
            _clients[provider] = new HttpClient(new ProviderHttpHandler(provider, transport(provider)))
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };
        }
    }

    public HttpClient For(LlmProvider provider) => _clients[provider];

    public void Dispose()
    {
        foreach (var client in _clients.Values)
        {
            client.Dispose();
        }
    }
}
