using DocAgent.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace DocAgent.Api.Data;

public static class DatabaseMigration
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Applies pending migrations in Development and Testing only (production migrations are a
    /// deploy step, Phase 8). If the database is unreachable the error is logged and startup
    /// continues, so /health/ready reports Unhealthy instead of the app crashing.
    /// </summary>
    public static async Task ApplyMigrationsIfNeededAsync(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Testing"))
        {
            return;
        }

        try
        {
            await using var scope = app.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // The Aspire EF integration retries failed connections with backoff (over a minute);
            // bound the wait so an unreachable database does not stall startup for that long.
            using var timeout = new CancellationTokenSource(StartupTimeout);
            await db.Database.MigrateAsync(timeout.Token);
        }
        catch (Exception ex)
        {
            app.Logger.LogError(ex, "Could not apply database migrations at startup; continuing.");
        }
    }
}
