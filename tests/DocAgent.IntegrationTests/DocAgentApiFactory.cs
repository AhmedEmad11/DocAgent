using DocAgent.Core.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace DocAgent.IntegrationTests;

/// <summary>
/// Runs the real API in-process, pointed at a test PostgreSQL database. Emails go to
/// <see cref="Emails"/> instead of SMTP. The rate limits (auth, key, ask, upload, shared demo throttle) are raised unless a test wants the real ones.
/// </summary>
public sealed class DocAgentApiFactory(
    string connectionString,
    IDictionary<string, string?>? settings = null,
    bool raiseRateLimit = true,
    Action<IServiceCollection>? configureServices = null,
    string environment = "Testing") : WebApplicationFactory<Program>
{
    public const string AllowedReturnUrl = "http://localhost:3000";

    public CapturingEmailSender Emails { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.UseSetting("ConnectionStrings:docagentdb", connectionString);
        builder.UseSetting("Authentication:External:AllowedReturnUrls:0", AllowedReturnUrl);
        if (raiseRateLimit)
        {
            builder.UseSetting("RateLimiting:Auth:PermitLimit", "10000");
            builder.UseSetting("RateLimiting:LlmKey:PermitLimit", "10000");
            builder.UseSetting("RateLimiting:Ask:MaxConcurrentPerUser", "10000");
            builder.UseSetting("RateLimiting:Upload:MaxConcurrentPerUser", "10000");
            builder.UseSetting("Llm:Demo:GlobalRequestsPerMinute", "0"); // tests that exercise the shared throttle set it themselves
        }

        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            builder.UseSetting(key, value);
        }

        // Catch scoped-service-from-root mistakes that Development runs would hit.
        builder.UseDefaultServiceProvider(o => o.ValidateScopes = true);

        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IEmailSender<AppUser>>(Emails);
            configureServices?.Invoke(services);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            Npgsql.NpgsqlConnection.ClearAllPools(); // release this test's connections right away
        }
    }
}
