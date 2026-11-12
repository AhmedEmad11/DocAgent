using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DocAgent.Core.Entities;
using DocAgent.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pgvector;

namespace DocAgent.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class AuthFlowTests(PostgresFixture postgres) : IAsyncLifetime
{
    private DocAgentApiFactory _factory = null!;
    private string _connectionString = null!;

    public async Task InitializeAsync()
    {
        _connectionString = await TestDatabase.CreateAsync(postgres); // API migrates it at startup (Testing env)
        _factory = new DocAgentApiFactory(_connectionString);
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Anonymous_me_returns_401()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Register_confirm_email_and_me_reports_confirmed()
    {
        using var client = _factory.CreateClient();
        var email = AuthTestHelpers.NewEmail();

        Assert.Equal(HttpStatusCode.OK, (await client.RegisterAsync(email)).StatusCode);

        // Signing in is allowed before confirming, but the account is flagged unconfirmed.
        var tokens = await client.LoginAsync(email);
        using var authed = _factory.CreateClient().WithBearer(tokens.AccessToken);
        var before = await authed.GetFromJsonAsync<JsonElement>("/api/v1/me");
        Assert.False(before.GetProperty("emailConfirmed").GetBoolean());
        Assert.Equal(email, before.GetProperty("email").GetString());
        Assert.False(before.GetProperty("hasLlmKey").GetBoolean());
        Assert.Equal(0, before.GetProperty("externalLogins").GetArrayLength());

        var link = _factory.Emails.Last("confirm", email).Value;
        var confirm = await client.GetAsync(link);
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);

        var after = await authed.GetFromJsonAsync<JsonElement>("/api/v1/me");
        Assert.True(after.GetProperty("emailConfirmed").GetBoolean());
    }

    [Fact]
    public async Task Login_returns_tokens_and_refresh_issues_new_ones()
    {
        using var client = _factory.CreateClient();
        var email = AuthTestHelpers.NewEmail();
        await client.RegisterAsync(email);

        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = AuthTestHelpers.Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var body = await login.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Bearer", body.GetProperty("tokenType").GetString());
        Assert.Equal(3600, body.GetProperty("expiresIn").GetInt32()); // D26: 1 hour
        var refreshToken = body.GetProperty("refreshToken").GetString();
        Assert.False(string.IsNullOrEmpty(body.GetProperty("accessToken").GetString()));

        var refreshed = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken });
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var refreshedBody = await refreshed.Content.ReadFromJsonAsync<JsonElement>();
        var newAccess = refreshedBody.GetProperty("accessToken").GetString()!;

        using var authed = _factory.CreateClient().WithBearer(newAccess);
        Assert.Equal(HttpStatusCode.OK, (await authed.GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task Wrong_password_is_rejected()
    {
        using var client = _factory.CreateClient();
        var email = AuthTestHelpers.NewEmail();
        await client.RegisterAsync(email);

        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "Wr0ng!pass" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Weak_password_is_rejected_at_registration()
    {
        using var client = _factory.CreateClient();
        var response = await client.RegisterAsync(AuthTestHelpers.NewEmail(), "short");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Forgot_password_then_reset_allows_login_with_new_password()
    {
        using var client = _factory.CreateClient();
        var email = AuthTestHelpers.NewEmail();
        await client.RegisterAsync(email);
        await client.GetAsync(_factory.Emails.Last("confirm", email).Value); // reset emails go to confirmed addresses only

        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsJsonAsync("/api/v1/auth/forgotPassword", new { email })).StatusCode);

        var resetCode = _factory.Emails.Last("resetCode", email).Value;
        const string newPassword = "N3wPassw0rd!";
        var reset = await client.PostAsJsonAsync("/api/v1/auth/resetPassword", new { email, resetCode, newPassword });
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);

        var oldLogin = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = AuthTestHelpers.Password });
        Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);
        var tokens = await client.LoginAsync(email, newPassword);
        Assert.False(string.IsNullOrEmpty(tokens.AccessToken));
    }

    [Fact]
    public async Task Delete_me_removes_user_and_cascades_to_owned_data()
    {
        using var client = _factory.CreateClient();
        var email = AuthTestHelpers.NewEmail();
        await client.RegisterAsync(email);
        var tokens = await client.LoginAsync(email);
        using var authed = _factory.CreateClient().WithBearer(tokens.AccessToken);
        var me = await authed.GetFromJsonAsync<JsonElement>("/api/v1/me");
        var userId = me.GetProperty("id").GetGuid();

        await using (var db = TestDatabase.CreateContext(_connectionString, userId))
        {
            var doc = new Document
            {
                Id = Guid.NewGuid(), UserId = userId, FileName = "a.pdf", ContentType = "application/pdf",
                SizeBytes = 1, Status = DocumentStatus.Ready, CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Documents.Add(doc);
            db.Chunks.Add(new Chunk
            {
                Id = Guid.NewGuid(), DocumentId = doc.Id, UserId = userId, Text = "t", EmbeddingModel = "m",
                Embedding = new Vector(new float[Chunk.EmbeddingDimensions]),
            });
            db.IngestionJobs.Add(new IngestionJob
            {
                Id = Guid.NewGuid(), DocumentId = doc.Id, UserId = userId, State = IngestionJobState.Queued,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            db.UserLlmCredentials.Add(new UserLlmCredential
            {
                UserId = userId, Provider = LlmProvider.Gemini, Model = "m", EncryptedKey = "e", KeyLast4 = "1234",
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            db.ExternalLoginCodes.Add(new ExternalLoginCode
            {
                Id = Guid.NewGuid(), CodeHash = "h", UserId = userId, CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1),
            });
            await db.SaveChangesAsync();
        }

        // hasLlmKey now true.
        Assert.True((await authed.GetFromJsonAsync<JsonElement>("/api/v1/me")).GetProperty("hasLlmKey").GetBoolean());

        var delete = await authed.DeleteAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        await using var check = TestDatabase.CreateContext(_connectionString);
        Assert.False(await check.Users.AnyAsync(u => u.Id == userId));
        Assert.False(await check.Documents.IgnoreQueryFilters().AnyAsync(d => d.UserId == userId));
        Assert.False(await check.Chunks.IgnoreQueryFilters().AnyAsync(c => c.UserId == userId));
        Assert.False(await check.IngestionJobs.IgnoreQueryFilters().AnyAsync(j => j.UserId == userId));
        Assert.False(await check.UserLlmCredentials.IgnoreQueryFilters().AnyAsync(c => c.UserId == userId));
        Assert.False(await check.ExternalLoginCodes.AnyAsync(c => c.UserId == userId));

        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = AuthTestHelpers.Password });
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    [Fact]
    public async Task Email_confirmed_policy_checks_the_database()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var confirmed = new AppUser { UserName = "c@example.com", Email = "c@example.com", EmailConfirmed = true };
        var unconfirmed = new AppUser { UserName = "u@example.com", Email = "u@example.com", EmailConfirmed = false };
        await users.CreateAsync(confirmed);
        await users.CreateAsync(unconfirmed);

        Assert.True(await Evaluate(users, confirmed));
        Assert.False(await Evaluate(users, unconfirmed));
        Assert.False(await Evaluate(users, null));
    }

    private static async Task<bool> Evaluate(UserManager<AppUser> users, AppUser? user)
    {
        var principal = user is null
            ? new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity())
            : new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, user.Id.ToString())],
                "test"));
        var requirement = new DocAgent.Api.Auth.EmailConfirmedRequirement();
        var context = new Microsoft.AspNetCore.Authorization.AuthorizationHandlerContext([requirement], principal, null);
        await new DocAgent.Api.Auth.EmailConfirmedHandler(users).HandleAsync(context);
        return context.HasSucceeded;
    }
}
