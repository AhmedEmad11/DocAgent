using System.ClientModel;
using System.ClientModel.Primitives;
using Anthropic;
using DocAgent.Core.Entities;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI;

namespace DocAgent.Infrastructure.Llm;

/// <summary>Builds the chat client for one provider, model and key. The key is only ever held by the SDK client.</summary>
public interface IProviderChatClientFactory
{
    /// <returns>A client with error translation, telemetry and logging applied (tool calling works through it).</returns>
    IChatClient Create(LlmProvider provider, string model, string apiKey);
}

/// <summary>
/// OpenRouter, OpenAI and Gemini go through the OpenAI SDK against their fixed OpenAI-compatible endpoints
/// (OpenRouter: /api/v1; Gemini: /v1beta/openai, which supports function calling). Anthropic uses the official Anthropic SDK.
/// All four are exposed as Microsoft.Extensions.AI <see cref="IChatClient"/> (SRS MAIN-1).
/// </summary>
public sealed class ProviderChatClientFactory(LlmHttpClients http, ILoggerFactory loggerFactory) : IProviderChatClientFactory
{
    public IChatClient Create(LlmProvider provider, string model, string apiKey)
    {
        var client = provider == LlmProvider.Anthropic
            ? CreateAnthropic(model, apiKey, http.For(provider))
            : CreateOpenAICompatible(provider, model, apiKey, http.For(provider));

        var safe = new ProviderErrorChatClient(client, loggerFactory.CreateLogger<ProviderErrorChatClient>());
        return ChatClientPipeline.Wrap(safe, loggerFactory);
    }

    private static IChatClient CreateOpenAICompatible(LlmProvider provider, string model, string apiKey, HttpClient httpClient)
    {
        var options = new OpenAIClientOptions
        {
            Endpoint = LlmProviders.Endpoint(provider),
            Transport = new HttpClientPipelineTransport(httpClient),
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0), // surface 429/5xx at once, with a specific message
            NetworkTimeout = TimeSpan.FromMinutes(5),
        };
        return new OpenAIClient(new ApiKeyCredential(apiKey), options).GetChatClient(model).AsIChatClient();
    }

    private static IChatClient CreateAnthropic(string model, string apiKey, HttpClient httpClient)
    {
        var client = new AnthropicClient
        {
            ApiKey = apiKey,
            BaseUrl = LlmProviders.Endpoint(LlmProvider.Anthropic).ToString().TrimEnd('/'),
            HttpClient = httpClient,
            MaxRetries = 0,
            Timeout = TimeSpan.FromMinutes(5),
        };
        // The SDK sends ApiKey as "Authorization: Bearer"; also send the documented x-api-key header so the call does not depend on that.
        var withHeader = client.WithOptions(o => o with { ExtraHeaders = new Dictionary<string, string> { ["x-api-key"] = apiKey } });
        return withHeader.AsIChatClient(model);
    }
}
