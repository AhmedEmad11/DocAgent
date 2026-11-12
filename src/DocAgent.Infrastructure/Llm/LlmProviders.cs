using DocAgent.Core.Entities;

namespace DocAgent.Infrastructure.Llm;

/// <summary>
/// The four supported providers and their fixed endpoints (SRS D7, KEY-8). Endpoints are constants: nothing a user sends
/// can change them, and <see cref="ProviderHttpHandler"/> refuses any request to another host.
/// </summary>
public static class LlmProviders
{
    public static IReadOnlyList<LlmProvider> All { get; } = [LlmProvider.OpenRouter, LlmProvider.Anthropic, LlmProvider.OpenAI, LlmProvider.Gemini];

    /// <summary>Base address used by the SDK (OpenAI-compatible providers: the chat-completions base, with a trailing slash).</summary>
    public static Uri Endpoint(LlmProvider provider) => provider switch
    {
        LlmProvider.OpenRouter => new Uri("https://openrouter.ai/api/v1/"),
        LlmProvider.OpenAI => new Uri("https://api.openai.com/v1/"),
        LlmProvider.Gemini => new Uri("https://generativelanguage.googleapis.com/v1beta/openai/"),
        LlmProvider.Anthropic => new Uri("https://api.anthropic.com/"),
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };

    /// <summary>The only host the provider's HTTP client may talk to.</summary>
    public static string Host(LlmProvider provider) => Endpoint(provider).Host;

    public static Uri ModelsEndpoint(LlmProvider provider) => provider switch
    {
        LlmProvider.OpenRouter => new Uri("https://openrouter.ai/api/v1/models"),
        LlmProvider.OpenAI => new Uri("https://api.openai.com/v1/models"),
        LlmProvider.Anthropic => new Uri("https://api.anthropic.com/v1/models?limit=1000"),
        LlmProvider.Gemini => new Uri("https://generativelanguage.googleapis.com/v1beta/models?pageSize=1000"),
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };

    /// <summary>Public name used in URLs, requests and responses: lower case.</summary>
    public static string Name(LlmProvider provider) => provider.ToString().ToLowerInvariant();

    /// <summary>Parses a provider name (case-insensitive). Numbers and unknown names are rejected.</summary>
    public static bool TryParse(string? value, out LlmProvider provider)
    {
        provider = default;
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || !trimmed.All(char.IsLetter))
        {
            return false;
        }

        return Enum.TryParse(trimmed, ignoreCase: true, out provider) && Enum.IsDefined(provider);
    }
}
