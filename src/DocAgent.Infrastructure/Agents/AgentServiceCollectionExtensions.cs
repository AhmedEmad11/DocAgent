using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DocAgent.Infrastructure.Agents;

public static class AgentServiceCollectionExtensions
{
    public static IServiceCollection AddAgents(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AgentOptions>(configuration.GetSection(AgentOptions.SectionName));
        services.AddScoped<DocumentWorkflows>();
        services.AddSingleton<AnswerVerifier>();
        services.AddScoped<AgentQaService>();
        services.AddScoped<AskService>();
        return services;
    }
}
