using DocAgent.Core.Retrieval;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DocAgent.Infrastructure.Retrieval;

public static class RetrievalServiceCollectionExtensions
{
    public static IServiceCollection AddRetrieval(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RetrievalOptions>(configuration.GetSection(RetrievalOptions.SectionName));
        services.AddScoped<IRetriever, Retriever>();
        return services;
    }
}
