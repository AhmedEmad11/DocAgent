using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace DocAgent.Api.Security;

/// <summary>
/// Per-user caps on the expensive endpoints (OWASP API4, unrestricted resource consumption). Concurrency limits, not request
/// rates: an upload buffers up to 25 MB in memory and an answer holds an in-process embedding, an agent loop and an open
/// connection for up to two minutes, so what has to be bounded is how many one account runs at once. A request over the cap is
/// refused at once with 429 (no queue). 0 switches a cap off. Demo users also have a daily quota and a shared throttle (DEMO-2).
/// </summary>
public static class ResourceRateLimiting
{
    public const string AskPolicy = "ask";
    public const string UploadPolicy = "upload";

    public const int DefaultAskConcurrency = 4;
    public const int DefaultUploadConcurrency = 2;

    public static IServiceCollection AddResourceRateLimits(this IServiceCollection services, IConfiguration config)
    {
        var ask = config.GetValue("RateLimiting:Ask:MaxConcurrentPerUser", DefaultAskConcurrency);
        var upload = config.GetValue("RateLimiting:Upload:MaxConcurrentPerUser", DefaultUploadConcurrency);
        services.Configure<RateLimiterOptions>(options =>
        {
            options.AddPolicy(AskPolicy, context => PerUser(context, ask));
            options.AddPolicy(UploadPolicy, context => PerUser(context, upload));
        });
        return services;
    }

    private static RateLimitPartition<string> PerUser(HttpContext context, int permits)
    {
        if (permits <= 0)
        {
            return RateLimitPartition.GetNoLimiter("unlimited");
        }

        var key = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetConcurrencyLimiter(key, _ => new ConcurrencyLimiterOptions { PermitLimit = permits, QueueLimit = 0 });
    }
}
