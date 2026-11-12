using System.Security.Cryptography;
using System.Text;

namespace DocAgent.Core.Auth;

/// <summary>Generation and hashing of single-use external-login codes.</summary>
public static class OneTimeCode
{
    public const int ByteLength = 32;

    /// <summary>Cryptographically random 32-byte code, base64url without padding (43 chars).</summary>
    public static string Generate() =>
        Base64UrlEncode(RandomNumberGenerator.GetBytes(ByteLength));

    /// <summary>SHA-256 of the code as lowercase hex (64 chars).</summary>
    public static string Hash(string code) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(code)));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
