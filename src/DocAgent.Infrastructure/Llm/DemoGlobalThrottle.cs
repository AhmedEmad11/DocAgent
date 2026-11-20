using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace DocAgent.Infrastructure.Llm;

/// <summary>
/// A local, all-users request limit for the demo route, in front of the shared operator key (Phase 7). The free OpenRouter
/// models have their own per-minute and per-day limits that every demo user shares; without this, a handful of users could
/// exhaust them for everyone and every request would still be sent out to be refused. Rejected requests cost no daily
/// question (the throttle runs before <see cref="DemoQuotaService"/>) and no provider call. The window is per process, so
/// with several API instances the effective limit is the sum; the free tier runs one.
/// <c>Llm:Demo:GlobalRequestsPerMinute</c> = 0 switches it off.
/// </summary>
public sealed class DemoGlobalThrottle : IDisposable
{
    private const int Segments = 6;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly SlidingWindowRateLimiter? _limiter;

    public DemoGlobalThrottle(IOptions<LlmOptions> options)
    {
        var perMinute = options.Value.Demo.GlobalRequestsPerMinute;
        if (perMinute > 0)
        {
            _limiter = new SlidingWindowRateLimiter(new SlidingWindowRateLimiterOptions
            {
                PermitLimit = perMinute,
                Window = Window,
                SegmentsPerWindow = Segments,
                QueueLimit = 0,
                AutoReplenishment = true,
            });
        }
    }

    /// <summary>Takes one demo request from the shared allowance. Throws a rate-limited <see cref="DemoUnavailableException"/> (429 <c>demo_rate_limited</c>) when it is used up.</summary>
    public void Acquire()
    {
        if (_limiter is null)
        {
            return;
        }

        using var lease = _limiter.AttemptAcquire();
        if (lease.IsAcquired)
        {
            return;
        }

        var retryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out var hint) && hint > TimeSpan.Zero
            ? hint
            : Window / Segments;
        throw new DemoUnavailableException(rateLimited: true, retryAfter);
    }

    public void Dispose() => _limiter?.Dispose();
}
