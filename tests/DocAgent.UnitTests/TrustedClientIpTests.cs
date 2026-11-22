using DocAgent.Core.Auth;

namespace DocAgent.UnitTests;

/// <summary>Plan A4 / D41: X-Client-IP is believed only together with the matching Bff:SharedSecret.</summary>
public sealed class TrustedClientIpTests
{
    private const string Secret = "example-only-bff-secret";

    [Fact]
    public void Header_with_matching_secret_is_used()
    {
        Assert.Equal("203.0.113.7", TrustedClientIp.Resolve("10.0.0.1", "203.0.113.7", Secret, Secret));
        Assert.Equal("2001:db8::1", TrustedClientIp.Resolve("10.0.0.1", " 2001:db8::1 ", Secret, Secret));
    }

    [Fact]
    public void Ipv4_mapped_address_is_normalised()
    {
        Assert.Equal("203.0.113.7", TrustedClientIp.Resolve("10.0.0.1", "::ffff:203.0.113.7", Secret, Secret));
    }

    [Theory]
    [InlineData("203.0.113.7", "wrong-secret-value-x")] // wrong secret
    [InlineData("203.0.113.7", null)]                    // no secret
    [InlineData("203.0.113.7", "")]
    [InlineData(null, Secret)]                           // no header
    [InlineData("", Secret)]
    [InlineData("not-an-ip", Secret)]                    // garbage
    [InlineData("203.0.113.7, 198.51.100.1", Secret)]    // lists are not accepted
    [InlineData("203.0.113.7:443", Secret)]
    public void Otherwise_the_connection_address_is_used(string? header, string? secretHeader)
    {
        Assert.Equal("10.0.0.1", TrustedClientIp.Resolve("10.0.0.1", header, secretHeader, Secret));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Without_a_configured_secret_the_header_is_never_trusted(string? configured)
    {
        Assert.Equal("10.0.0.1", TrustedClientIp.Resolve("10.0.0.1", "203.0.113.7", "anything", configured));
        Assert.Equal("10.0.0.1", TrustedClientIp.Resolve("10.0.0.1", "203.0.113.7", "", configured));
    }

    [Fact]
    public void Missing_connection_address_falls_back_to_unknown()
    {
        Assert.Equal("unknown", TrustedClientIp.Resolve(null, null, null, Secret));
    }

    [Fact]
    public void Short_secrets_are_rejected_and_empty_means_disabled()
    {
        Assert.Throws<InvalidOperationException>(() => TrustedClientIp.ValidateConfiguredSecret("short"));
        TrustedClientIp.ValidateConfiguredSecret(null);
        TrustedClientIp.ValidateConfiguredSecret("");
        TrustedClientIp.ValidateConfiguredSecret(Secret);
    }
}
