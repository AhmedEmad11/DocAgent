using System.Net;
using System.Net.Http.Json;
using DocAgent.Core.Auth;

namespace DocAgent.IntegrationTests;

/// <summary>Plan A4 / D41: the per-IP auth rate limit uses X-Client-IP only together with the matching Bff:SharedSecret.</summary>
[Collection(PostgresCollection.Name)]
public sealed class BffClientIpTests(PostgresFixture postgres)
{
    private const string Secret = "example-only-bff-secret";

    private async Task<DocAgentApiFactory> FactoryAsync(bool withSecret)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Documents:Ingestion:WorkerEnabled"] = "false",
            ["RateLimiting:Auth:PermitLimit"] = "2",
            ["RateLimiting:Auth:WindowSeconds"] = "600",
        };
        if (withSecret)
        {
            settings["Bff:SharedSecret"] = Secret;
        }

        return new DocAgentApiFactory(await TestDatabase.CreateAsync(postgres), settings, raiseRateLimit: false);
    }

    private static async Task<HttpStatusCode> LoginAsync(HttpClient client, string? clientIp, string? secret)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new { email = "nobody@example.com", password = "Example-Wrong-3" }),
        };
        if (clientIp is not null)
        {
            request.Headers.Add(TrustedClientIp.IpHeader, clientIp);
        }

        if (secret is not null)
        {
            request.Headers.Add(TrustedClientIp.SecretHeader, secret);
        }

        return (await client.SendAsync(request)).StatusCode;
    }

    [Fact]
    public async Task With_the_secret_each_forwarded_ip_gets_its_own_bucket()
    {
        await using var factory = await FactoryAsync(withSecret: true);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, await LoginAsync(client, "203.0.113.1", Secret));
        Assert.Equal(HttpStatusCode.Unauthorized, await LoginAsync(client, "203.0.113.1", Secret));
        Assert.Equal(HttpStatusCode.TooManyRequests, await LoginAsync(client, "203.0.113.1", Secret));

        // A different visitor behind the same BFF is not throttled by the first one.
        Assert.Equal(HttpStatusCode.Unauthorized, await LoginAsync(client, "203.0.113.2", Secret));
    }

    [Fact]
    public async Task Without_a_matching_secret_the_header_is_ignored()
    {
        await using var factory = await FactoryAsync(withSecret: true);
        using var client = factory.CreateClient();

        // Rotating spoofed addresses all land in the real connection's bucket.
        Assert.Equal(HttpStatusCode.Unauthorized, await LoginAsync(client, "198.51.100.1", null));
        Assert.Equal(HttpStatusCode.Unauthorized, await LoginAsync(client, "198.51.100.2", "wrong-secret-value-x"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await LoginAsync(client, "198.51.100.3", null));
    }

    [Fact]
    public async Task When_no_secret_is_configured_the_header_is_never_trusted()
    {
        await using var factory = await FactoryAsync(withSecret: false);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, await LoginAsync(client, "198.51.100.1", Secret));
        Assert.Equal(HttpStatusCode.Unauthorized, await LoginAsync(client, "198.51.100.2", Secret));
        Assert.Equal(HttpStatusCode.TooManyRequests, await LoginAsync(client, "198.51.100.3", Secret));
    }
}
