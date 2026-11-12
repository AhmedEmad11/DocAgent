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
}
