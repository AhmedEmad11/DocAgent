using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Http.Features;

namespace DocAgent.Api.Security;

/// <summary>
/// HTTP-level hardening (SRS SEC-4, OWASP API8): response headers, request body size, CORS and forwarded headers.
/// All of it is configuration with safe defaults; see README "Security" for the settings.
/// </summary>
public static class HttpHardening
{
    /// <summary>The one endpoint that takes large bodies; its own limit comes from the caller's tier (D19).</summary>
    public const string UploadPath = "/api/v1/documents";

    public const string CorsPolicy = "ui";

    /// <summary>JSON bodies on every other endpoint: a question is at most 2,000 characters, a key at most 512.</summary>
    public const long DefaultMaxJsonBodyBytes = 256 * 1024;

    // ---- response headers ----

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            if (!headers.ContainsKey("Cache-Control"))
            {
                headers["Cache-Control"] = "no-store"; // tokens, document text and answers must not sit in shared caches
            }

            // A JSON API needs no scripts, frames or images. The development test page (/dev) is the exception: it has an inline script.
            if (!context.Request.Path.StartsWithSegments("/dev"))
            {
                headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
            }

            return Task.CompletedTask;
        });

        await next();
    });

    // ---- request size ----

    /// <summary>
    /// Caps the body of every request except the upload at <c>Limits:MaxJsonBodyBytes</c> (default 256 KB) before anything reads it:
    /// by the declared length (413 problem details) and, for chunked bodies, through the server's per-request limit.
    /// Without this the server default (about 30 MB) applies to every JSON endpoint.
    /// </summary>
    public static IApplicationBuilder UseRequestBodyLimit(this IApplicationBuilder app, IConfiguration configuration)
    {
        var limit = configuration.GetValue("Limits:MaxJsonBodyBytes", DefaultMaxJsonBodyBytes);
        return app.Use(async (context, next) =>
        {
            var isUpload = HttpMethods.IsPost(context.Request.Method) && context.Request.Path.Equals(UploadPath, StringComparison.OrdinalIgnoreCase);
            if (!isUpload)
            {
                if (context.Request.ContentLength is { } declared && declared > limit)
                {
                    context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                    await context.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
                    {
                        HttpContext = context,
                        ProblemDetails =
                        {
                            Status = StatusCodes.Status413PayloadTooLarge,
                            Title = "Request too large",
                            Detail = $"The request body is larger than the {limit / 1024} KB limit.",
                        },
                    });
                    return;
                }

                var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
                if (feature is { IsReadOnly: false })
                {
                    feature.MaxRequestBodySize = limit;
                }
            }

            await next();
        });
    }

    // ---- CORS ----

    /// <summary>
    /// CORS is off unless <c>Cors:AllowedOrigins</c> lists origins (the Next.js UI, Phase 9). Only those exact origins, the methods
    /// and headers the API uses, no credentials (the API uses bearer tokens, not cookies).
    /// </summary>
    public static IServiceCollection AddUiCors(this IServiceCollection services, IConfiguration configuration)
    {
        var origins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()?
            .Where(o => !string.IsNullOrWhiteSpace(o)).Select(o => o.TrimEnd('/')).ToArray() ?? [];
        if (origins.Any(o => o == "*"))
        {
            throw new InvalidOperationException("Cors:AllowedOrigins must list explicit origins; '*' is not allowed.");
        }

        if (origins.Length > 0)
        {
            services.AddCors(options => options.AddPolicy(CorsPolicy, policy => policy
                .WithOrigins(origins)
                .WithMethods("GET", "POST", "PUT", "DELETE")
                .WithHeaders("Authorization", "Content-Type", "X-Llm-Api-Key")
                .WithExposedHeaders("Retry-After")
                .SetPreflightMaxAge(TimeSpan.FromMinutes(10))));
        }

        return services;
    }

    public static IApplicationBuilder UseUiCors(this IApplicationBuilder app, IConfiguration configuration)
    {
        var any = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()?.Any(o => !string.IsNullOrWhiteSpace(o)) == true;
        return any ? app.UseCors(CorsPolicy) : app;
    }

    // ---- reverse proxy ----

    /// <summary>
    /// Behind a TLS-terminating proxy (Azure Container Apps) the app sees plain HTTP from the proxy's address: HTTPS-only behaviour (HSTS,
    /// redirects) and the per-IP auth rate limit would use the wrong scheme and the proxy's IP for every user. <c>ForwardedHeaders:Enabled=true</c>
    /// trusts X-Forwarded-For/-Proto from any sender: set it only when the app is reachable only through that proxy (Phase 8).
    /// </summary>
    public static IServiceCollection AddForwardedHeadersIfEnabled(this IServiceCollection services, IConfiguration configuration)
    {
        if (configuration.GetValue("ForwardedHeaders:Enabled", false))
        {
            services.Configure<ForwardedHeadersOptions>(options =>
            {
                options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
                options.ForwardLimit = configuration.GetValue("ForwardedHeaders:ForwardLimit", 1);
                options.KnownIPNetworks.Clear();
                options.KnownProxies.Clear();
            });
        }

        return services;
    }

    public static IApplicationBuilder UseForwardedHeadersIfEnabled(this IApplicationBuilder app, IConfiguration configuration) =>
        configuration.GetValue("ForwardedHeaders:Enabled", false) ? app.UseForwardedHeaders() : app;
}
