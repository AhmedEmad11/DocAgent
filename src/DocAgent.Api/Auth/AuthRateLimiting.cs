using System.Threading.RateLimiting;
using DocAgent.Core.Auth;
using Microsoft.AspNetCore.RateLimiting;

namespace DocAgent.Api.Auth;

/// <summary>Fixed-window, per-client-IP rate limit on credential-handling endpoints (SRS SEC-8, D27).</summary>
public static class AuthRateLimiting
{
    private const string Prefix = "/api/v1/auth/";

    private static readonly HashSet<string> LimitedPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "register", "login", "refresh", "forgotPassword", "resetPassword", "resendConfirmationEmail",
        "external/exchange",
    };

    public static IServiceCollection AddAuthRateLimiting(this IServiceCollection services, IConfiguration config)
    {
        var permitLimit = config.GetValue("RateLimiting:Auth:PermitLimit", 10);
        var windowSeconds = config.GetValue("RateLimiting:Auth:WindowSeconds", 60);
        var bffSecret = config["Bff:SharedSecret"];
        TrustedClientIp.ValidateConfiguredSecret(bffSecret); // A4: refuse a short secret at startup

        services.AddRateLimiter(options =>
        {
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                if (!IsLimited(context.Request.Path))
                {
                    return RateLimitPartition.GetNoLimiter("none");
                }

                // A4: behind the Next.js BFF the visitor's address arrives in X-Client-IP, trusted only with the matching shared secret.
                var ip = TrustedClientIp.Resolve(
                    context.Connection.RemoteIpAddress?.ToString(),
                    context.Request.Headers[TrustedClientIp.IpHeader].ToString(),
                    context.Request.Headers[TrustedClientIp.SecretHeader].ToString(),
                    bffSecret);
                return RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permitLimit,
                    Window = TimeSpan.FromSeconds(windowSeconds),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                });
            });

            options.OnRejected = async (context, ct) =>
            {
                var http = context.HttpContext;
                http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    http.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
                }
                else
                {
                    http.Response.Headers.RetryAfter = "2"; // concurrency caps carry no hint: a slot frees up when a running request ends
                }

                await http.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(
                    new ProblemDetailsContext
                    {
                        HttpContext = http,
                        ProblemDetails =
                        {
                            Status = StatusCodes.Status429TooManyRequests,
                            Title = "Too many requests",
                            Detail = "Too many attempts. Try again later.",
                        },
                    });
            };
        });

        return services;
    }

    public static bool IsLimited(PathString path)
    {
        if (!path.StartsWithSegments(Prefix.TrimEnd('/'), out var rest) || rest.Value is not { Length: > 1 } value)
        {
            return false;
        }

        var tail = value.Trim('/');
        if (LimitedPaths.Contains(tail))
        {
            return true;
        }

        // External sign-in start: /external/{provider}; the provider callback is not limited.
        return tail.StartsWith("external/", StringComparison.OrdinalIgnoreCase)
            && !tail.Equals("external/callback", StringComparison.OrdinalIgnoreCase)
            && tail.IndexOf('/', "external/".Length) < 0;
    }
}
