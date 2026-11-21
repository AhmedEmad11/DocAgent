using DocAgent.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace DocAgent.Api.Data;

public static class DatabaseMigration
{
    public const string MigrateOnStartupKey = "Database:MigrateOnStartup";
    public const string MigrationTimeoutKey = "Database:MigrationTimeoutSeconds";

    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(15);
    private const int DefaultProductionTimeoutSeconds = 300; // a Neon compute that scaled to zero needs a few seconds to wake

    /// <summary>
    /// Development and Testing apply pending migrations at startup; if the database is unreachable the error is logged and startup
    /// continues, so /health/ready reports Unhealthy instead of the app crashing.
    /// Anywhere else (Production) migrations are the deploy step (<c>--migrate</c>) unless <c>Database:MigrateOnStartup=true</c> opts in, for hosts
    /// without a pre-deploy hook (SnapDeploy): then they are applied once, under a Postgres advisory lock (parallel starts wait for the first),
    /// and any failure stops the startup with a clear message instead of serving a half-migrated schema.
    /// </summary>
    public static async Task ApplyMigrationsIfNeededAsync(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Testing"))
        {
            if (app.Configuration.GetValue(MigrateOnStartupKey, false))
            {
                await MigrateOrFailAsync(app);
            }

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

    private static async Task MigrateOrFailAsync(WebApplication app)
    {
        var connectionString = app.Configuration.GetConnectionString("docagentdb");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException($"{MigrateOnStartupKey}=true but no connection string is set (ConnectionStrings__docagentdb).");
        }

        var timeout = TimeSpan.FromSeconds(Math.Max(5, app.Configuration.GetValue(MigrationTimeoutKey, DefaultProductionTimeoutSeconds)));
        try
        {
            var applied = await MigrationRunner.ApplyAsync(
                connectionString, timeout,
                pending => app.Logger.LogWarning("Applying {Count} database migration(s) at startup: {Migrations}", pending.Count, string.Join(", ", pending)),
                app.Lifetime.ApplicationStopping);
            app.Logger.LogInformation(applied.Count == 0
                ? "Database is up to date (Database:MigrateOnStartup=true)."
                : "Applied {Count} database migration(s) at startup.", applied.Count);
        }
        catch (Exception ex)
        {
            // Type and message only: Npgsql messages name the host but never carry the password.
            throw new InvalidOperationException(
                $"Startup stopped: applying the database migrations failed ({ex.GetType().Name}: {ex.Message}). "
                + $"Check ConnectionStrings__docagentdb and that the database is reachable, or set {MigrateOnStartupKey}=false and run 'DocAgent.Api --migrate' as a deploy step.", ex);
        }
    }
}
