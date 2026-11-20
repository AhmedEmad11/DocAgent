using DocAgent.Infrastructure.Llm;
using Microsoft.Extensions.Options;

namespace DocAgent.UnitTests.Hardening;

public sealed class DemoGlobalThrottleTests
{
    private static DemoGlobalThrottle Create(int perMinute) =>
        new(Options.Create(new LlmOptions { Demo = new DemoOptions { GlobalRequestsPerMinute = perMinute } }));

    [Fact]
    public void Default_is_twenty_requests_per_minute_for_all_users()
    {
        Assert.Equal(20, new DemoOptions().GlobalRequestsPerMinute);
    }

    [Fact]
    public void Requests_over_the_limit_are_refused_as_demo_rate_limited_with_a_retry_hint()
    {
        using var throttle = Create(3);
        for (var i = 0; i < 3; i++)
        {
            throttle.Acquire();
        }

        var ex = Assert.Throws<DemoUnavailableException>(throttle.Acquire);

        Assert.Equal(LlmFailureKind.DemoRateLimited, ex.Kind);
        Assert.NotNull(ex.RetryAfter);
        Assert.True(ex.RetryAfter > TimeSpan.Zero);
        Assert.True(ex.RetryAfter <= TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void Zero_switches_the_limit_off()
    {
        using var throttle = Create(0);
        for (var i = 0; i < 500; i++)
        {
            throttle.Acquire();
        }
    }
}
