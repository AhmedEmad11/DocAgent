using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DocAgent.Infrastructure.Llm;

public static class LlmServiceCollectionExtensions
{
    /// <summary>Registers the chat-client factory for the configured provider (<c>Llm:Provider</c>).</summary>
    public static IServiceCollection AddLlm(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<LlmOptions>(configuration.GetSection(LlmOptions.SectionName));
        var provider = configuration[$"{LlmOptions.SectionName}:Provider"] ?? "Ollama";
        switch (provider)
        {
            case "Ollama":
                services.AddSingleton<ILlmClientFactory, OllamaChatClientFactory>();
                break;
            default:
                throw new InvalidOperationException($"Unsupported Llm:Provider '{provider}'. Supported: Ollama.");
        }

        return services;
    }
}
