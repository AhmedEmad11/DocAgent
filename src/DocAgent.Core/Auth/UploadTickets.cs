using System.Security.Cryptography;
using System.Text;

namespace DocAgent.Core.Auth;

/// <summary>
/// Short-lived, single-use tickets that let the browser post one file straight to <c>POST /api/v1/documents</c> without holding an
/// access or refresh token (SRS D40, plan A3). The Next.js backend-for-frontend asks for a ticket with the user's token; the browser sends the
/// file with <c>X-Upload-Ticket</c>. A ticket is bound to the user who asked for it and is accepted on that one route only (the caller, not
/// this class, enforces the route). Held in memory: the API runs as one instance (in-process throttles and the ingestion worker already
/// require that), and a restart only invalidates tickets that live for a minute anyway. Only SHA-256 hashes of tickets are kept.
/// </summary>
public sealed class UploadTicketService(TimeProvider time)
{
    public const string Header = "X-Upload-Ticket";
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    /// <summary>Unused tickets one user may hold at once; asking for more drops the oldest (bounds memory).</summary>
    public const int MaxOutstandingPerUser = 5;

    private readonly object gate = new();
    private readonly Dictionary<string, Entry> tickets = [];

    private readonly record struct Entry(Guid UserId, DateTimeOffset Expires, DateTimeOffset Issued);

    public (string Ticket, int ExpiresInSeconds) Issue(Guid userId)
    {
        var ticket = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var now = time.GetUtcNow();
        lock (gate)
        {
            foreach (var expired in tickets.Where(t => t.Value.Expires <= now).Select(t => t.Key).ToList())
            {
                tickets.Remove(expired);
            }

            var mine = tickets.Where(t => t.Value.UserId == userId).OrderBy(t => t.Value.Issued).Select(t => t.Key).ToList();
            foreach (var oldest in mine.Take(Math.Max(0, mine.Count - MaxOutstandingPerUser + 1)))
            {
                tickets.Remove(oldest);
            }

            tickets[Hash(ticket)] = new Entry(userId, now + Lifetime, now);
        }

        return (ticket, (int)Lifetime.TotalSeconds);
    }

    /// <summary>Returns the user the ticket was issued to and uses it up; null for an unknown, used or expired ticket.</summary>
    public Guid? Redeem(string? ticket)
    {
        if (string.IsNullOrWhiteSpace(ticket) || ticket.Length > 128)
        {
            return null;
        }

        var key = Hash(ticket.Trim());
        lock (gate)
        {
            if (!tickets.Remove(key, out var entry))
            {
                return null;
            }

            return entry.Expires > time.GetUtcNow() ? entry.UserId : null;
        }
    }

    private static string Hash(string ticket) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ticket)));
}
