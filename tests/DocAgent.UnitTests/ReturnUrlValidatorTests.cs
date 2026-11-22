using DocAgent.Core.Auth;

namespace DocAgent.UnitTests;

public sealed class ReturnUrlValidatorTests
{
    private static readonly string[] Allowed =
        ["http://localhost:3000", "http://localhost:5248/dev/external-login-test", "https://app.example.com/app"];

    [Theory]
    [InlineData("http://localhost:3000")]
    [InlineData("http://localhost:3000/")]
    [InlineData("http://localhost:3000/some/path?x=1")]
    [InlineData("HTTP://LOCALHOST:3000/")]
    [InlineData("http://localhost:5248/dev/external-login-test")]
    [InlineData("http://localhost:5248/dev/external-login-test/")]
    [InlineData("https://app.example.com/app/callback")]
    public void Allows_urls_under_an_allowed_prefix(string url)
    {
        Assert.True(ReturnUrlValidator.TryValidate(url, Allowed, out var validated));
        Assert.NotNull(validated);
    }

    [Theory]
    [InlineData("http://evil.example/")]                              // wrong host
    [InlineData("http://localhost.evil.example:3000/")]                // look-alike host
    [InlineData("http://localhost:3001/")]                             // wrong port
    [InlineData("http://localhost/")]                                  // default port differs
    [InlineData("https://localhost:3000/")]                            // wrong scheme
    [InlineData("http://localhost:5248/")]                             // path outside the prefix
    [InlineData("http://localhost:5248/dev/external-login-testing")]   // not a segment boundary
    [InlineData("http://localhost:3000@evil.example/")]                // userinfo trick
    [InlineData("http://localhost:3000/#frag")]                        // fragments not allowed
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,hi")]
    [InlineData("//evil.example/")]                                    // scheme-relative
    [InlineData("/relative/path")]
    [InlineData("ftp://localhost:3000/")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Rejects_everything_else(string? url)
    {
        Assert.False(ReturnUrlValidator.TryValidate(url, Allowed, out var validated));
        Assert.Null(validated);
    }

    [Fact]
    public void Rejects_everything_when_allowlist_is_empty()
    {
        Assert.False(ReturnUrlValidator.TryValidate("http://localhost:3000", [], out _));
    }

    [Fact]
    public void Ignores_malformed_allowlist_entries()
    {
        Assert.False(ReturnUrlValidator.TryValidate("http://localhost:3000", ["not a url", "javascript:alert(1)"], out _));
    }

    /// <summary>Plan A2: the UI callback URL (with the login-CSRF nonce in the query) is allowed by the real Development settings, and nothing else on that origin.</summary>
    [Fact]
    public void Development_settings_allow_exactly_the_ui_callback()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "DocAgent.Api", "appsettings.Development.json")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!.FullName, "src", "DocAgent.Api", "appsettings.Development.json")));
        var allowed = doc.RootElement.GetProperty("Authentication").GetProperty("External").GetProperty("AllowedReturnUrls")
            .EnumerateArray().Select(e => e.GetString()!).ToArray();

        Assert.True(ReturnUrlValidator.TryValidate("http://localhost:3000/api/auth/external/callback?s=nonce123", allowed, out _));
        Assert.False(ReturnUrlValidator.TryValidate("http://localhost:3000/", allowed, out _));
        Assert.False(ReturnUrlValidator.TryValidate("http://localhost:3000/en/documents", allowed, out _));
        Assert.False(ReturnUrlValidator.TryValidate("http://localhost:3000/api/auth/external/callback-evil", allowed, out _));
    }
}
