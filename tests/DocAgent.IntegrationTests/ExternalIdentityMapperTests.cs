using System.Security.Claims;
using DocAgent.Api.Auth;

namespace DocAgent.IntegrationTests;

public sealed class ExternalIdentityMapperTests
{
    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "test"));

    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("false", false)]
    [InlineData("", false)]
    public void Google_verification_follows_email_verified_claim(string claim, bool expected)
    {
        var identity = ExternalIdentityMapper.From("Google", "1", Principal(
            (ClaimTypes.Email, "a@example.com"), (ExternalIdentityMapper.GoogleEmailVerifiedClaim, claim)));
        Assert.Equal("a@example.com", identity.Email);
        Assert.Equal(expected, identity.EmailVerified);
    }

    [Fact]
    public void Google_without_the_claim_is_unverified()
    {
        var identity = ExternalIdentityMapper.From("Google", "1", Principal((ClaimTypes.Email, "a@example.com")));
        Assert.False(identity.EmailVerified);
    }

    [Fact]
    public void GitHub_ignores_the_plain_email_claim_and_trusts_only_the_verified_one()
    {
        var plain = ExternalIdentityMapper.From("GitHub", "1", Principal((ClaimTypes.Email, "a@example.com")));
        Assert.Null(plain.Email);
        Assert.False(plain.EmailVerified);

        var verified = ExternalIdentityMapper.From("GitHub", "1", Principal(
            (ClaimTypes.Email, "other@example.com"), (ExternalIdentityMapper.GitHubVerifiedEmailClaim, "v@example.com")));
        Assert.Equal("v@example.com", verified.Email);
        Assert.True(verified.EmailVerified);
    }
}
