using Microsoft.Extensions.AI;

namespace DocAgent.Infrastructure.Llm;

/// <summary>Which route served a request (SRS QA-13): "development", "demo" or "byok".</summary>
public sealed record LlmRouteInfo(string Mode, string Provider, string Model);

/// <summary>A ready chat client (telemetry and logging already applied) together with a description of its route.</summary>
/// <param name="Options">Provider-specific request defaults; callers clone them and add their own settings.</param>
public sealed record LlmRoute(IChatClient Client, LlmRouteInfo Info, ChatOptions? Options = null);

/// <summary>
/// Chooses the chat client for the current request. Phase 3 always returns the development client (Ollama);
/// per-user BYOK clients (Phase 5) and the demo client (Phase 6) plug in here without touching the Q&amp;A code.
/// </summary>
public interface ILlmClientFactory
{
    ValueTask<LlmRoute> GetRouteAsync(CancellationToken ct = default);
}
