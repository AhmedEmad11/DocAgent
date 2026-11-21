using DocAgent.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DocAgent.Api.Data;

/// <summary>
/// Applies pending EF Core migrations under a Postgres advisory lock, so several instances starting at once (a rolling deploy, a restart loop)
/// migrate exactly once: the others wait for the lock, then find nothing pending. Shared by <see cref="MigrateCommand"/> (<c>--migrate</c>)
/// and the opt-in startup migration (<c>Database:MigrateOnStartup</c>, see <see cref="DatabaseMigration"/>).
/// </summary>
public static class MigrationRunner
{
    /// <summary>The advisory lock key ("docagent" in ASCII); EF Core's own migration lock uses a different key.</summary>
    public const long AdvisoryLockKey = 0x646F_6361_6765_6E74;

    private static readonly TimeSpan LockPollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>Returns the names of the migrations that were applied (empty = the database was already up to date).</summary>
    public static async Task<IReadOnlyList<string>> ApplyAsync(
        string connectionString, TimeSpan timeout, Action<IReadOnlyList<string>>? onPending = null, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        var ct = cts.Token;

        // The lock lives as long as this connection (session-level); migrations run on their own connection.
        await using var lockConnection = new NpgsqlConnection(connectionString);
        await lockConnection.OpenAsync(ct);
        await AcquireLockAsync(lockConnection, ct);
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(connectionString, npgsql => npgsql.UseVector().CommandTimeout((int)timeout.TotalSeconds))
                .Options;
            await using var db = new AppDbContext(options);

            var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
            if (pending.Count == 0)
            {
                return [];
            }

            onPending?.Invoke(pending);
            await db.Database.MigrateAsync(ct);
            return pending;
        }
        finally
        {
            try
            {
                await using var unlock = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", lockConnection);
                unlock.Parameters.AddWithValue("key", AdvisoryLockKey);
                await unlock.ExecuteScalarAsync(CancellationToken.None);
            }
            catch
            {
                // closing the connection releases the lock anyway
            }
        }
    }

    private static async Task AcquireLockAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        while (true)
        {
            await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", connection);
            command.Parameters.AddWithValue("key", AdvisoryLockKey);
            if ((bool)(await command.ExecuteScalarAsync(ct))!)
            {
                return;
            }

            await Task.Delay(LockPollInterval, ct);
        }
    }
}
