using DocAgent.Core.Entities;
using DocAgent.Infrastructure.Agents;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocAgent.Infrastructure.Llm;

/// <summary>
/// The route for a signed-in user who has no saved key (SRS KEY-6, D17): the local Ollama model in Development/Testing,
/// the OpenRouter demo model (<see cref="DemoNoKeyRouteProvider"/>) elsewhere. Selected with <c>Llm:NoKeyRoute</c>.
/// </summary>
public interface INoKeyRouteProvider
{
    /// <summary>True for the demo route: its limits (daily quota, privacy notice) apply and it is reported by GET /me.</summary>
    bool IsDemo => false;

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

/// <summary>
/// The demo route (SRS DEMO-1..4): charges one question of the user's daily allowance, then serves the request from the
/// configured free OpenRouter model (with its fallback) on the operator's key, with the <c>Agent:Limits:Demo</c> caps.
/// Scoped; the route is resolved once per request, so one request costs one question.
/// </summary>
public sealed class DemoNoKeyRouteProvider(
    IProviderChatClientFactory providers,
    DemoQuotaService quota,
    IOptions<LlmOptions> llmOptions,
    IOptions<AgentOptions> agentOptions,
    ILoggerFactory loggerFactory) : INoKeyRouteProvider
{
    /// <summary>PRIV-1: shown with every demo answer and by GET /me.</summary>
    public const string PrivacyNotice =
        "Demo mode: your question and the document excerpts used to answer it are sent to a free third-party language model "
        + "through OpenRouter. The model's provider may log them and use them. Do not use demo mode for confidential documents; "
        + "add your own LLM API key (PUT /api/v1/me/llm-key) to choose your provider.";

    public bool IsDemo => true;

    public async ValueTask<LlmRoute?> GetRouteAsync(CancellationToken ct = default)
    {
        var demo = llmOptions.Value.Demo;
        var charge = await quota.ChargeAsync(ct); // before any model call; throws DemoQuotaExceededException

        var key = demo.OpenRouterApiKey!; // required at startup (AddLlm)
        var primary = providers.Create(LlmProvider.OpenRouter, demo.Model, key);
        var hasFallback = !string.IsNullOrWhiteSpace(demo.FallbackModel) && demo.FallbackModel != demo.Model;
        var fallback = hasFallback ? providers.Create(LlmProvider.OpenRouter, demo.FallbackModel!, key) : null;

        var client = new DemoChatClient(
            primary, demo.Model, fallback, hasFallback ? demo.FallbackModel : null,
            loggerFactory.CreateLogger<DemoChatClient>(), () => quota.RefundAsync(charge));

        return new LlmRoute(client, client.CurrentInfo(), Options: null, agentOptions.Value.Limits.Demo.ToLimits())
        {
            InfoSource = client.CurrentInfo,
        };
    }
}
