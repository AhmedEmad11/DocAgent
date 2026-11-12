using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DocAgent.Core.Auth;
using DocAgent.Core.Entities;
using DocAgent.Infrastructure.Auth;
using DocAgent.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocAgent.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class ExternalLoginEndpointTests(PostgresFixture postgres) : IAsyncLifetime
{
    private DocAgentApiFactory _factory = null!;
    private string _connectionString = null!;

    public async Task InitializeAsync()
    {
        _connectionString = await TestDatabase.CreateAsync(postgres);
        _factory = new DocAgentApiFactory(_connectionString, new Dictionary<string, string?>
        {
            // Fake credentials: enough to register the Google handler; nothing contacts Google
            // because the start endpoint only builds the redirect.
            ["Authentication:Google:ClientId"] = "test-client-id.apps.googleusercontent.com",
            ["Authentication:Google:ClientSecret"] = "test-secret",
        });
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private HttpClient NoRedirectClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private async Task<Guid> CreateUserAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = new AppUser { UserName = AuthTestHelpers.NewEmail(), EmailConfirmed = true };
        user.Email = user.UserName;
        Assert.True((await users.CreateAsync(user)).Succeeded);
        return user.Id;
    }

    // ---- start endpoint ----

    [Fact]
    public async Task Start_with_allowed_return_url_redirects_to_google()
    {
        using var client = NoRedirectClient();
        var response = await client.GetAsync("/api/v1/auth/external/google?returnUrl=" + Uri.EscapeDataString("http://localhost:3000/after"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.ToString();
        Assert.StartsWith("https://accounts.google.com/", location);
        Assert.Contains("client_id=test-client-id", location);
        Assert.Contains("%2Fsignin-google", location); // registered callback path is unchanged
        Assert.Contains("scope=", location);
    }

    [Theory]
    [InlineData("http://evil.example/")]
    [InlineData("http://localhost:3001/")]
    [InlineData("https://localhost:3000/")]
    [InlineData("javascript:alert(1)")]
    [InlineData("")]
    public async Task Start_rejects_disallowed_or_missing_return_url(string returnUrl)
    {
        using var client = NoRedirectClient();
        var response = await client.GetAsync("/api/v1/auth/external/google?returnUrl=" + Uri.EscapeDataString(returnUrl));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Start_without_return_url_param_is_rejected()
    {
        using var client = NoRedirectClient();
        var response = await client.GetAsync("/api/v1/auth/external/google");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("github")]   // not configured in this factory
    [InlineData("facebook")] // unknown
    public async Task Start_for_unconfigured_or_unknown_provider_is_404_problem_details(string provider)
    {
        using var client = NoRedirectClient();
        var response = await client.GetAsync($"/api/v1/auth/external/{provider}?returnUrl=" + Uri.EscapeDataString("http://localhost:3000"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    // ---- exchange endpoint ----

    [Fact]
    public async Task Exchange_valid_code_returns_tokens_that_work()
    {
        var userId = await CreateUserAsync();
        string code;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            code = await scope.ServiceProvider.GetRequiredService<ExternalLoginCodeService>().IssueAsync(userId);
        }

        // Only the hash is stored.
        await using (var db = TestDatabase.CreateContext(_connectionString))
        {
            var row = await db.ExternalLoginCodes.SingleAsync(c => c.UserId == userId);
            Assert.NotEqual(code, row.CodeHash);
            Assert.Equal(OneTimeCode.Hash(code), row.CodeHash);
            Assert.Null(row.UsedAt);
        }

        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/external/exchange", new { code });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var access = body.GetProperty("accessToken").GetString()!;
        Assert.False(string.IsNullOrEmpty(body.GetProperty("refreshToken").GetString()));
        Assert.Equal(3600, body.GetProperty("expiresIn").GetInt32());

        using var authed = _factory.CreateClient().WithBearer(access);
        var me = await authed.GetFromJsonAsync<JsonElement>("/api/v1/me");
        Assert.Equal(userId, me.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Exchange_reused_code_is_rejected()
    {
        var userId = await CreateUserAsync();
        string code;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            code = await scope.ServiceProvider.GetRequiredService<ExternalLoginCodeService>().IssueAsync(userId);
        }

        using var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/auth/external/exchange", new { code })).StatusCode);

        var again = await client.PostAsJsonAsync("/api/v1/auth/external/exchange", new { code });
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Equal("application/problem+json", again.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Exchange_concurrent_redemptions_succeed_exactly_once()
    {
        var userId = await CreateUserAsync();
        string code;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            code = await scope.ServiceProvider.GetRequiredService<ExternalLoginCodeService>().IssueAsync(userId);
        }

        using var client = _factory.CreateClient();
        var responses = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => client.PostAsJsonAsync("/api/v1/auth/external/exchange", new { code })));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(7, responses.Count(r => r.StatusCode == HttpStatusCode.BadRequest));
    }

    [Fact]
    public async Task Exchange_expired_code_is_rejected()
    {
        var userId = await CreateUserAsync();
        const string code = "expired-code-for-test";
        await using (var db = TestDatabase.CreateContext(_connectionString))
        {
            db.ExternalLoginCodes.Add(new ExternalLoginCode
            {
                Id = Guid.NewGuid(), CodeHash = OneTimeCode.Hash(code), UserId = userId,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-4),
            });
            await db.SaveChangesAsync();
        }

        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/external/exchange", new { code });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("""{"code":"never-issued"}""")]
    [InlineData("""{"code":""}""")]
    [InlineData("""{}""")]
    public async Task Exchange_unknown_or_missing_code_is_rejected(string json)
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsync("/api/v1/auth/external/exchange",
            new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }
}
