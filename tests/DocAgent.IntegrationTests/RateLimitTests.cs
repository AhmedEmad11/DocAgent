using System.Net;
using System.Net.Http.Json;

namespace DocAgent.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class RateLimitTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Eleventh_login_attempt_within_a_minute_is_rejected_with_429_problem_details()
    {
        var cs = await TestDatabase.CreateAsync(postgres);
        await using var factory = new DocAgentApiFactory(cs, raiseRateLimit: false); // real default: 10 per minute
        using var client = factory.CreateClient();
        var body = new { email = "nobody@example.com", password = "Wr0ng!pass" };

        for (var i = 1; i <= 10; i++)
        {
            var ok = await client.PostAsJsonAsync("/api/v1/auth/login", body);
            Assert.Equal(HttpStatusCode.Unauthorized, ok.StatusCode);
        }

        var limited = await client.PostAsJsonAsync("/api/v1/auth/login", body);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("application/problem+json", limited.Content.Headers.ContentType?.MediaType);
        Assert.True(limited.Headers.Contains("Retry-After"));

        // Endpoints outside the list are unaffected.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
    }
}
