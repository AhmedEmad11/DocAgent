using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DocAgent.Infrastructure.Llm;

public static class LlmServiceCollectionExtensions
{
    /// <summary>
    /// Registers per-request route selection (user key -> BYOK client, otherwise the no-key route), the four providers,
    /// key encryption, validation and model listing. <paramref name="allowDevelopmentRoute"/>: Development/Testing environment.
    /// </summary>
    public static IServiceCollection AddLlm(this IServiceCollection services, IConfiguration configuration, bool allowDevelopmentRoute)
    {
        services.Configure<LlmOptions>(configuration.GetSection(LlmOptions.SectionName));
        var provider = configuration[$"{LlmOptions.SectionName}:Provider"] ?? "Ollama";
        if (provider != "Ollama")
        {
            throw new InvalidOperationException($"Unsupported Llm:Provider '{provider}'. Supported: Ollama.");
        }

        services.AddSingleton<OllamaChatClientFactory>();

        // No-key route: Auto = Ollama in Development/Testing, none elsewhere. Phase 6 adds "Demo" (OpenRouter on the operator's key).
        var noKey = configuration[$"{LlmOptions.SectionName}:NoKeyRoute"] ?? "Auto";
        switch (noKey)
        {
            case "Auto" when allowDevelopmentRoute:
            case "Ollama":
                services.AddSingleton<INoKeyRouteProvider, OllamaNoKeyRouteProvider>();
                break;
            case "Auto":
            case "None":
                services.AddSingleton<INoKeyRouteProvider, NoNoKeyRouteProvider>();
                break;
            default:
                throw new InvalidOperationException($"Unsupported Llm:NoKeyRoute '{noKey}'. Supported: Auto, Ollama, None.");
        }

        services.AddDataProtection().SetApplicationName("DocAgent");
        services.AddSingleton<ILlmKeyProtector, LlmKeyProtector>();
        services.AddSingleton<LlmHttpClients>();
        services.AddSingleton<IProviderChatClientFactory, ProviderChatClientFactory>();
        services.AddSingleton<LlmKeyValidator>();
        services.AddSingleton<LlmModelCatalog>();
        services.AddScoped<ILlmClientFactory, UserLlmClientFactory>();
        return services;
    }
}
