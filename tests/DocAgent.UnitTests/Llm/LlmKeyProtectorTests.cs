using DocAgent.TestSupport;
using DocAgent.Infrastructure.Llm;
using Microsoft.AspNetCore.DataProtection;

namespace DocAgent.UnitTests.Llm;

public sealed class LlmKeyProtectorTests
{
    private readonly LlmKeyProtector _protector = new(new EphemeralDataProtectionProvider());

    [Fact]
    public void Round_trip_returns_the_key_and_the_payload_does_not_contain_it()
    {
        var user = Guid.NewGuid();

        var payload = _protector.Protect(user, LlmTest.SecretKey);

        Assert.DoesNotContain("SECRET", payload);
        Assert.Equal(LlmTest.SecretKey, _protector.Unprotect(user, payload));
    }

    [Fact]
    public void A_payload_copied_to_another_user_does_not_decrypt()
    {
        var payload = _protector.Protect(Guid.NewGuid(), LlmTest.SecretKey);

        Assert.Null(_protector.Unprotect(Guid.NewGuid(), payload));
    }

    [Theory]
    [InlineData("")]
    [InlineData("enc")]
    [InlineData("CfDJ8-not-a-real-payload")]
    public void Damaged_payloads_give_null_instead_of_throwing(string payload) =>
        Assert.Null(_protector.Unprotect(Guid.NewGuid(), payload));

    [Fact]
    public void A_different_key_ring_cannot_read_the_payload()
    {
        var user = Guid.NewGuid();
        var payload = _protector.Protect(user, LlmTest.SecretKey);

        Assert.Null(new LlmKeyProtector(new EphemeralDataProtectionProvider()).Unprotect(user, payload));
    }
}
