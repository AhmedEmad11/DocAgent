using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace DocAgent.IntegrationTests;

/// <summary>
/// Plan A8: the Identity API behaviour the Next.js BFF depends on (SRS 10.6), pinned so a framework upgrade that changes it fails here
/// and not in the UI. Facts: register/reset errors are validation problems keyed by Identity error code; an unconfirmed account can sign in;
/// /refresh issues a NEW refresh token and does not revoke the old one (stateless tickets); the sealed pair is small enough for one cookie.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class IdentityContractTests(PostgresFixture postgres) : IAsyncLifetime
{
    private DocAgentApiFactory _factory = null!;

    public async Task InitializeAsync() =>
        _factory = new DocAgentApiFactory(await TestDatabase.CreateAsync(postgres));

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private static async Task<JsonElement> ErrorsAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(400, body.GetProperty("status").GetInt32());
        return body.GetProperty("errors");
    }

    [Fact]
    public async Task Register_password_rule_failures_are_keyed_by_identity_error_code()
    {
        using var client = _factory.CreateClient();
        var errors = await ErrorsAsync(await client.RegisterAsync(AuthTestHelpers.NewEmail(), "abc"));

        foreach (var code in new[] { "PasswordTooShort", "PasswordRequiresNonAlphanumeric", "PasswordRequiresDigit", "PasswordRequiresUpper" })
        {
            Assert.True(errors.TryGetProperty(code, out var messages), code);
            Assert.Equal(JsonValueKind.Array, messages.ValueKind);
        }

        Assert.False(errors.TryGetProperty("PasswordRequiresLower", out _)); // "abc" has lowercase letters
    }

    [Fact]
    public async Task Register_duplicate_email_reports_DuplicateUserName_and_DuplicateEmail()
    {
        using var client = _factory.CreateClient();
        var email = AuthTestHelpers.NewEmail();
        Assert.Equal(HttpStatusCode.OK, (await client.RegisterAsync(email)).StatusCode);

        var errors = await ErrorsAsync(await client.RegisterAsync(email)); // the API itself does reveal that the address is taken
        Assert.True(errors.TryGetProperty("DuplicateUserName", out _));
        Assert.True(errors.TryGetProperty("DuplicateEmail", out _));
    }

    [Fact]
    public async Task Unconfirmed_account_can_sign_in_and_login_failure_is_401_with_detail()
    {
        using var client = _factory.CreateClient();
        var email = AuthTestHelpers.NewEmail();
        await client.RegisterAsync(email);

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = AuthTestHelpers.Password })).StatusCode);

        var wrong = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "Example-Wrong-3" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        var body = await wrong.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Failed", body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Reset_with_a_bad_code_reports_InvalidToken()
    {
        using var client = _factory.CreateClient();
        var email = AuthTestHelpers.NewEmail();
        await client.RegisterAsync(email);
        var errors = await ErrorsAsync(await client.PostAsJsonAsync("/api/v1/auth/resetPassword",
            new { email, resetCode = "not-a-code", newPassword = "Example-Only-2" }));
        Assert.True(errors.TryGetProperty("InvalidToken", out _));
    }

    [Fact]
    public async Task Confirm_email_with_a_bad_code_is_401()
    {
        using var client = _factory.CreateClient();
        var email = AuthTestHelpers.NewEmail();
        await client.RegisterAsync(email);
        var link = _factory.Emails.Last("confirm", email).Value;
        var bad = link[..link.IndexOf("code=", StringComparison.Ordinal)] + "code=AAAA";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(bad)).StatusCode);
    }

    [Fact]
    public async Task Refresh_issues_a_new_refresh_token_and_the_old_one_keeps_working()
    {
        using var client = _factory.CreateClient();
        var email = AuthTestHelpers.NewEmail();
        await client.RegisterAsync(email);
        var first = await client.LoginAsync(email);

        var refreshed = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = first.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var second = (await refreshed.Content.ReadFromJsonAsync<Tokens>(AuthTestHelpers.JsonOptions))!;
        Assert.NotEqual(first.RefreshToken, second.RefreshToken);
        Assert.NotEqual(first.AccessToken, second.AccessToken);

        // Not rotated server-side: concurrent refreshes with the same old token both succeed (the BFF relies on this, plan 9.1).
        var again = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = first.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    [Fact]
    public async Task Token_pair_fits_one_session_cookie()
    {
        using var client = _factory.CreateClient();
        var email = AuthTestHelpers.NewEmail();
        await client.RegisterAsync(email);
        var tokens = await client.LoginAsync(email);

        // The BFF seals both tokens into one cookie (AES-GCM, base64url: about 1.37 x the plaintext). Its hard limit is 3,800 characters.
        Assert.InRange(tokens.AccessToken.Length + tokens.RefreshToken.Length, 1, 2_600);
    }
}
