using DocAgent.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace DocAgent.Api.Data;

/// <summary>
/// <c>DocAgent.Api --migrate</c> (the container image: <c>docker run IMAGE --migrate</c>): applies pending EF Core migrations and exits.
/// It is the production deploy step (Phase 8): the app itself never migrates outside Development/Testing. It needs only the connection
/// string (<c>ConnectionStrings__docagentdb</c> environment variable or <c>--ConnectionStrings:docagentdb=...</c>), not the other production secrets,
/// and never prints it. Exit code 0 = database up to date, 1 = failed, 2 = no connection string.
/// </summary>
public static class MigrateCommand
{
    public const string Flag = "--migrate";
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5); // a Neon compute that scaled to zero needs a few seconds to wake

    public static bool IsRequested(string[] args) => args.Contains(Flag, StringComparer.OrdinalIgnoreCase);

    public static async Task<int> RunAsync(string[] args, TextWriter output, CancellationToken cancellationToken = default)
    {
        var configuration = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .AddCommandLine(args.Where(a => !a.Equals(Flag, StringComparison.OrdinalIgnoreCase)).ToArray())
            .Build();
        return await RunAsync(configuration.GetConnectionString("docagentdb"), output, cancellationToken);
    }

    public static async Task<int> RunAsync(string? connectionString, TextWriter output, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            await output.WriteLineAsync("ERROR: no connection string. Set ConnectionStrings__docagentdb.");
            return 2;
        }

        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(connectionString, npgsql => npgsql.UseVector().CommandTimeout((int)Timeout.TotalSeconds))
                .Options;
            await using var db = new AppDbContext(options);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);

            var pending = (await db.Database.GetPendingMigrationsAsync(timeout.Token)).ToList();
            if (pending.Count == 0)
            {
                await output.WriteLineAsync("Database is up to date; nothing to apply.");
                return 0;
            }

            await output.WriteLineAsync($"Applying {pending.Count} migration(s): {string.Join(", ", pending)}");
            await db.Database.MigrateAsync(timeout.Token);
            await output.WriteLineAsync("Migrations applied.");
            return 0;
        }
        catch (Exception ex)
        {
            // Type and message only: Npgsql messages name the host but never carry the password.
            await output.WriteLineAsync($"ERROR: migration failed: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }
}
