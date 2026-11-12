using DocAgent.TestSupport;
using DocAgent.Core.Entities;
using DocAgent.Infrastructure.Llm;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace DocAgent.UnitTests.Llm;

/// <summary>
/// Runs against the real provider only when its key is in the environment, otherwise skipped:
/// DOCAGENT_LIVE_OPENROUTER_KEY, DOCAGENT_LIVE_ANTHROPIC_KEY, DOCAGENT_LIVE_OPENAI_KEY, DOCAGENT_LIVE_GEMINI_KEY, each with an
/// optional DOCAGENT_LIVE_{PROVIDER}_MODEL. Each test makes a few cheap calls: key validation, model listing, and a tool-calling round trip.
/// The key is never printed.
/// </summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute(string provider)
    {
        if (string.IsNullOrWhiteSpace(LiveProviders.Key(provider)))
        {
            Skip = $"Set DOCAGENT_LIVE_{provider.ToUpperInvariant()}_KEY to run the live {provider} smoke test.";
        }
    }
}

public static class LiveProviders
{
    public static string? Key(string provider) => Environment.GetEnvironmentVariable($"DOCAGENT_LIVE_{provider.ToUpperInvariant()}_KEY");

    public static string Model(LlmProvider provider) =>
        Environment.GetEnvironmentVariable($"DOCAGENT_LIVE_{provider.ToString().ToUpperInvariant()}_MODEL") is { Length: > 0 } m
            ? m
            : provider switch
            {
                LlmProvider.OpenRouter => "openai/gpt-4o-mini",
                LlmProvider.Anthropic => "claude-haiku-4-5",
                LlmProvider.OpenAI => "gpt-4o-mini",
                _ => "gemini-2.5-flash",
            };
}

public sealed class LiveProviderSmokeTests
{
    [LiveFact("openrouter")]
    public Task OpenRouter() => RunAsync(LlmProvider.OpenRouter, "openrouter");

    [LiveFact("anthropic")]
    public Task Anthropic() => RunAsync(LlmProvider.Anthropic, "anthropic");

    [LiveFact("openai")]
    public Task OpenAI() => RunAsync(LlmProvider.OpenAI, "openai");

    [LiveFact("gemini")]
    public Task Gemini() => RunAsync(LlmProvider.Gemini, "gemini");

    private static async Task RunAsync(LlmProvider provider, string envName)
    {
        var key = LiveProviders.Key(envName)!.Trim();
        var model = LiveProviders.Model(provider);
        using var clients = new LlmHttpClients();
        var factory = new ProviderChatClientFactory(clients, NullLoggerFactory.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        // 1. KEY-2 validation call.
        await new LlmKeyValidator(factory).ValidateAsync(provider, model, key, cts.Token);

        // 2. KEY-5 model list contains something.
        var models = await new LlmModelCatalog(clients).ListToolModelsAsync(provider, key, cts.Token);
        Assert.NotEmpty(models);

        // 3. A real tool call through function invocation.
        var called = false;
        var tool = AIFunctionFactory.Create(() =>
        {
            called = true;
            return "The magic number is 4711.";
        }, "get_magic_number", "Returns the magic number. Call it whenever asked for the magic number.");
        using var client = factory.Create(provider, model, key).AsBuilder().UseFunctionInvocation().Build();
        var response = await client.GetResponseAsync(
            "What is the magic number? Use the tool.", new ChatOptions { Tools = [tool], MaxOutputTokens = 300 }, cts.Token);

        Assert.True(called, "The model did not call the tool.");
        Assert.Contains("4711", response.Text);

        // 4. KEY-7: a wrong key is reported as an invalid key (the provider's own error text is not exposed).
        var ex = await Assert.ThrowsAsync<LlmProviderException>(() =>
            new LlmKeyValidator(factory).ValidateAsync(provider, model, "invalid-key-for-smoke-test-0000", cts.Token));
        Assert.Equal(LlmFailureKind.InvalidKey, ex.Kind);
    }
}
