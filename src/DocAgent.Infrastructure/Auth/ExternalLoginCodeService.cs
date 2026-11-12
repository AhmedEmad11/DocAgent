using DocAgent.Core.Auth;
using DocAgent.Core.Entities;
using DocAgent.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace DocAgent.Infrastructure.Auth;

/// <summary>Issues and redeems single-use external-login codes (SRS D24). Only SHA-256 hashes are stored.</summary>
public sealed class ExternalLoginCodeService(AppDbContext db, TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    /// <summary>Creates a code for the user and returns the raw code (shown once, never stored).</summary>
    public async Task<string> IssueAsync(Guid userId, CancellationToken ct = default)
    {
        var code = OneTimeCode.Generate();
        var now = time.GetUtcNow();
        db.ExternalLoginCodes.Add(new ExternalLoginCode
        {
            Id = Guid.NewGuid(),
            CodeHash = OneTimeCode.Hash(code),
            UserId = userId,
            CreatedAt = now,
            ExpiresAt = now + Lifetime,
        });
        await db.SaveChangesAsync(ct);
        return code;
    }

    /// <summary>
    /// Redeems a code and returns its user id, or null if unknown, expired or already used.
    /// The claim is a single conditional UPDATE, so concurrent redemptions cannot both succeed.
    /// </summary>
    public async Task<Guid?> RedeemAsync(string? code, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 256)
        {
            return null;
        }

        var hash = OneTimeCode.Hash(code);
        var now = time.GetUtcNow();

        var claimed = await db.ExternalLoginCodes
            .Where(c => c.CodeHash == hash && c.UsedAt == null && c.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.UsedAt, now), ct);
        if (claimed != 1)
        {
            return null;
        }

        return await db.ExternalLoginCodes
            .Where(c => c.CodeHash == hash)
            .Select(c => (Guid?)c.UserId)
            .SingleOrDefaultAsync(ct);
    }
}
