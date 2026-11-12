using DocAgent.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace DocAgent.Infrastructure.Ingestion;

public sealed record ClaimedJob(Guid JobId, Guid DocumentId, Guid UserId, int Attempts);

/// <summary>
/// Postgres-backed job queue. Runs in the background worker, which has no HTTP user, so it works with
/// raw SQL / IgnoreQueryFilters on purpose (always scoped by job or document id).
/// </summary>
public sealed class IngestionJobQueue(AppDbContext db, IOptions<IngestionOptions> options)
{
    private const string ClaimSql = """
        WITH next AS (
            SELECT "Id" FROM "IngestionJobs"
            WHERE "State" = 'Queued'
            ORDER BY "CreatedAt"
            LIMIT 1
            FOR UPDATE SKIP LOCKED)
        UPDATE "IngestionJobs" AS j
        SET "State" = 'Running', "Attempts" = j."Attempts" + 1, "UpdatedAt" = now()
        FROM next
        WHERE j."Id" = next."Id"
        RETURNING j."Id", j."DocumentId", j."UserId", j."Attempts"
        """;

    /// <summary>Atomically takes the oldest queued job (several workers never get the same job).</summary>
    public async Task<ClaimedJob?> ClaimNextAsync(CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var command = (NpgsqlCommand)db.Database.GetDbConnection().CreateCommand();
            command.CommandText = ClaimSql;
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                return null;
            }

            return new ClaimedJob(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetInt32(3));
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>
    /// REL-3: jobs left Running by a crash or restart are put back in the queue, or failed when they have
    /// already used all their attempts (a document that keeps crashing the process must not loop forever).
    /// Assumes a single worker instance owns the queue at startup.
    /// </summary>
    public async Task<int> RecoverInterruptedAsync(CancellationToken ct)
    {
        var maxAttempts = options.Value.MaxAttempts;
        var failed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "IngestionJobs" SET "State" = 'Failed', "UpdatedAt" = now(),
                   "LastError" = 'Processing was interrupted repeatedly.'
            WHERE "State" = 'Running' AND "Attempts" >= {maxAttempts}
            """, ct);
        await db.Database.ExecuteSqlRawAsync("""
            UPDATE "Documents" SET "Status" = 'Failed',
                   "FailureReason" = 'Processing was interrupted repeatedly. Please upload the file again.'
            WHERE "Id" IN (SELECT "DocumentId" FROM "IngestionJobs"
                           WHERE "State" = 'Failed' AND "LastError" = 'Processing was interrupted repeatedly.')
              AND "Status" IN ('Pending', 'Processing')
            """, ct);
        await db.Database.ExecuteSqlRawAsync("""
            DELETE FROM "DocumentUploads"
            WHERE "DocumentId" IN (SELECT "DocumentId" FROM "IngestionJobs"
                                   WHERE "State" = 'Failed' AND "LastError" = 'Processing was interrupted repeatedly.')
            """, ct);
        var requeued = await db.Database.ExecuteSqlRawAsync("""
            UPDATE "IngestionJobs" SET "State" = 'Queued', "UpdatedAt" = now() WHERE "State" = 'Running'
            """, ct);
        await db.Database.ExecuteSqlRawAsync("""
            UPDATE "Documents" SET "Status" = 'Pending'
            WHERE "Status" = 'Processing'
              AND "Id" IN (SELECT "DocumentId" FROM "IngestionJobs" WHERE "State" = 'Queued')
            """, ct);
        return requeued + failed;
    }
}
