using Microsoft.Extensions.AI;

namespace DocAgent.Infrastructure.Llm;

/// <summary>Which route served a request (SRS QA-13): "development", "demo" or "byok".</summary>
public sealed record LlmRouteInfo(string Mode, string Provider, string Model);

/// <summary>Per-route agent caps (SRS QA-11): tool calls per question and the overall time for one question.</summary>
public sealed record AgentLimits(int MaxToolCalls, TimeSpan Timeout);

/// <summary>A ready chat client (telemetry and logging already applied) together with a description of its route.</summary>
/// <param name="Options">Provider-specific request defaults; callers clone them and add their own settings.</param>
/// <param name="Limits">Agent caps that travel with the route (development/BYOK: 10 calls / 120 s, demo: 4 / 60 s). Null = the development defaults.</param>
public sealed record LlmRoute(IChatClient Client, LlmRouteInfo Info, ChatOptions? Options = null, AgentLimits? Limits = null);

/// <summary>
/// Chooses the chat client for the current request: the signed-in user's saved key (BYOK, SRS KEY-6), otherwise the
/// no-key route (<see cref="INoKeyRouteProvider"/>: Ollama in development, the demo client in Phase 6).
/// Throws <see cref="LlmAccessException"/> when the user has to add a key or the saved key cannot be read.
/// </summary>
public interface ILlmClientFactory
{
    ValueTask<LlmRoute> GetRouteAsync(CancellationToken ct = default);
}
