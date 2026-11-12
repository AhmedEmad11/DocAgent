using DocAgent.Infrastructure.Agents;
using Microsoft.Extensions.Options;

namespace DocAgent.Infrastructure.Llm;

/// <summary>
/// The route for a signed-in user who has no saved key (SRS KEY-6). Phase 5: the local Ollama model in Development and
/// Testing, nothing elsewhere. Phase 6 adds an OpenRouter demo implementation and selects it with <c>Llm:NoKeyRoute=Demo</c>.
/// </summary>
public interface INoKeyRouteProvider
{
    /// <returns>The route, or null when there is none (the caller then answers 403 "add a key").</returns>
    ValueTask<LlmRoute?> GetRouteAsync(CancellationToken ct = default);
}

/// <summary>No fallback: users must bring a key.</summary>
public sealed class NoNoKeyRouteProvider : INoKeyRouteProvider
{
    public ValueTask<LlmRoute?> GetRouteAsync(CancellationToken ct = default) => ValueTask.FromResult<LlmRoute?>(null);
}

/// <summary>The development Ollama route as the no-key route.</summary>
public sealed class OllamaNoKeyRouteProvider(OllamaChatClientFactory ollama) : INoKeyRouteProvider
{
    public async ValueTask<LlmRoute?> GetRouteAsync(CancellationToken ct = default) => await ollama.GetRouteAsync(ct);
}
