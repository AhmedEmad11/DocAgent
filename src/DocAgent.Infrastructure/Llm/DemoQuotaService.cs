using DocAgent.Core.Abstractions;
using DocAgent.Core.Entities;
using DocAgent.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DocAgent.Infrastructure.Llm;

/// <summary>One demo question that was charged; used to give it back when the demo model failed before answering.</summary>
public sealed record DemoCharge(Guid UserId, DateOnly Day);

public sealed record DemoQuotaStatus(int Limit, int Used, int Remaining, DateTimeOffset ResetsAt);

/// <summary>
/// The per-user daily demo allowance (SRS DEMO-2), kept in the database so it survives restarts. A "day" is the UTC calendar
/// day. Charging is one conditional UPSERT, so parallel requests of one user can never push the count above the limit.
/// </summary>
public sealed class DemoQuotaService(AppDbContext db, ICurrentUser currentUser, TimeProvider time, IOptions<LlmOptions> options)
{
    private int Limit => Math.Max(0, options.Value.Demo.DailyQuestionLimit);

    public static DateOnly DayOf(DateTimeOffset now) => DateOnly.FromDateTime(now.UtcDateTime);

    public static DateTimeOffset ResetOf(DateOnly day) => new(day.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    /// <summary>Takes one question from today's allowance. Throws <see cref="DemoQuotaExceededException"/> when none is left.</summary>
    public async Task<DemoCharge> ChargeAsync(CancellationToken ct)
    {
        var userId = currentUser.UserId ?? throw new NoLlmKeyException();
        var now = time.GetUtcNow();
        var day = DayOf(now);
        var limit = Limit;

        // 1 row affected = charged. When the row exists and is already at the limit the WHERE clause skips the update (0 rows).
        var charged = limit > 0 && await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "DemoUsages" ("UserId", "Day", "Count") VALUES ({userId}, {day}, 1)
            ON CONFLICT ("UserId", "Day") DO UPDATE SET "Count" = "DemoUsages"."Count" + 1
            WHERE "DemoUsages"."Count" < {limit}
            """, ct) == 1;

        if (!charged)
        {
            var resetsAt = ResetOf(day);
            throw new DemoQuotaExceededException(limit, resetsAt, resetsAt - now);
        }

        return new DemoCharge(userId, day);
    }

    /// <summary>Gives a charged question back (never below zero). Not cancellable on purpose: it runs when a request is already failing.</summary>
    public async Task RefundAsync(DemoCharge charge) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "DemoUsages" SET "Count" = "Count" - 1
            WHERE "UserId" = {charge.UserId} AND "Day" = {charge.Day} AND "Count" > 0
            """, CancellationToken.None);

    public async Task<DemoQuotaStatus> GetStatusAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var day = DayOf(now);
        var used = await db.DemoUsages.AsNoTracking().Where(u => u.Day == day).Select(u => u.Count).FirstOrDefaultAsync(ct); // tenant filter: this user only
        var limit = Limit;
        used = Math.Min(used, limit);
        return new DemoQuotaStatus(limit, used, limit - used, ResetOf(day));
    }
}
