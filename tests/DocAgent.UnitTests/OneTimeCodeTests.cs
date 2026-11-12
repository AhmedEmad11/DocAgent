using System.Text.RegularExpressions;
using DocAgent.Core.Auth;

namespace DocAgent.UnitTests;

public sealed partial class OneTimeCodeTests
{
    [GeneratedRegex("^[A-Za-z0-9_-]{43}$")]
    private static partial Regex Base64Url32Bytes();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Hex();

    [Fact]
    public void Generated_code_is_32_bytes_of_base64url_without_padding()
    {
        var code = OneTimeCode.Generate();
        Assert.Matches(Base64Url32Bytes(), code);
        Assert.Equal(32, Convert.FromBase64String(code.Replace('-', '+').Replace('_', '/') + "=").Length);
    }

    [Fact]
    public void Generated_codes_are_unique()
    {
        var codes = Enumerable.Range(0, 1000).Select(_ => OneTimeCode.Generate()).ToHashSet();
        Assert.Equal(1000, codes.Count);
    }

    [Fact]
    public void Hash_is_lowercase_sha256_hex_and_differs_from_the_code()
    {
        var code = OneTimeCode.Generate();
        var hash = OneTimeCode.Hash(code);
        Assert.Matches(Sha256Hex(), hash);
        Assert.NotEqual(code, hash);
        Assert.DoesNotContain(code, hash);
    }

    [Fact]
    public void Hash_is_deterministic_and_input_sensitive()
    {
        Assert.Equal(OneTimeCode.Hash("abc"), OneTimeCode.Hash("abc"));
        Assert.NotEqual(OneTimeCode.Hash("abc"), OneTimeCode.Hash("abd"));
        // Known SHA-256 vector for "abc".
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", OneTimeCode.Hash("abc"));
    }
}
