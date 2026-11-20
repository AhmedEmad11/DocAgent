using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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

        // No-key route: Auto = Ollama in Development/Testing, the OpenRouter demo elsewhere (Phase 6).
        var noKey = configuration[$"{LlmOptions.SectionName}:NoKeyRoute"] ?? "Auto";
        switch (noKey)
        {
            case "Auto" when allowDevelopmentRoute:
            case "Ollama":
                services.AddSingleton<INoKeyRouteProvider, OllamaNoKeyRouteProvider>();
                break;
            case "Auto":
            case "Demo":
                // Fail fast: a deployment without the operator's key would otherwise only fail when the first user asks.
                var demo = configuration.GetSection($"{LlmOptions.SectionName}:Demo").Get<DemoOptions>() ?? new DemoOptions();
                if (string.IsNullOrWhiteSpace(demo.OpenRouterApiKey))
                {
                    throw new InvalidOperationException(
                        "Demo mode needs the operator's OpenRouter key in 'Llm:Demo:OpenRouterApiKey' (user-secrets in development, a secret in production). "
                        + "To run without a demo (users must bring their own key), set 'Llm:NoKeyRoute' to 'None'.");
                }

                if (string.IsNullOrWhiteSpace(demo.Model))
                {
                    throw new InvalidOperationException("'Llm:Demo:Model' must name an OpenRouter model.");
                }

                services.AddScoped<INoKeyRouteProvider, DemoNoKeyRouteProvider>();
                break;
            case "None":
                services.AddSingleton<INoKeyRouteProvider, NoNoKeyRouteProvider>();
                break;
            default:
                throw new InvalidOperationException($"Unsupported Llm:NoKeyRoute '{noKey}'. Supported: Auto, Ollama, Demo, None.");
        }

        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<DemoQuotaService>();

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
