using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DocAgent.Infrastructure.Qa;

public static class QaServiceCollectionExtensions
{
    public static IServiceCollection AddQa(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<QaOptions>(configuration.GetSection(QaOptions.SectionName));
        services.AddScoped<QaService>();
        return services;
    }
}
