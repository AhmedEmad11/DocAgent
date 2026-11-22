using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace DocAgent.Core.Auth;

/// <summary>
/// Client IP for the per-IP auth rate limit when the API sits behind the Next.js backend-for-frontend (SRS D41, plan A4).
/// All users then reach the API from the BFF host's addresses, so the BFF forwards the visitor's address in <c>X-Client-IP</c>.
/// The API believes that header only when the request also carries <c>X-Bff-Secret</c> equal to the configured <c>Bff:SharedSecret</c>;
/// anyone else cannot choose their own rate-limit bucket by sending the header.
/// </summary>
public static class TrustedClientIp
{
    public const string IpHeader = "X-Client-IP";
    public const string SecretHeader = "X-Bff-Secret";

    /// <summary>Shortest accepted <c>Bff:SharedSecret</c>; shorter values are a startup error.</summary>
    public const int MinSecretLength = 16;

    /// <param name="remoteIp">The connection's address (after the forwarded-headers middleware, if enabled).</param>
    /// <param name="clientIpHeader">The raw <c>X-Client-IP</c> value, or null.</param>
    /// <param name="secretHeader">The raw <c>X-Bff-Secret</c> value, or null.</param>
    /// <param name="configuredSecret"><c>Bff:SharedSecret</c>; null or empty disables the feature.</param>
    public static string Resolve(string? remoteIp, string? clientIpHeader, string? secretHeader, string? configuredSecret)
    {
        var fallback = string.IsNullOrEmpty(remoteIp) ? "unknown" : remoteIp;
        if (string.IsNullOrEmpty(configuredSecret)
            || string.IsNullOrEmpty(secretHeader)
            || string.IsNullOrWhiteSpace(clientIpHeader)
            || !SecretsMatch(secretHeader, configuredSecret)
            || !IPAddress.TryParse(clientIpHeader.Trim(), out var ip))
        {
            return fallback;
        }

        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        return ip.ToString();
    }

    public static void ValidateConfiguredSecret(string? secret)
    {
        if (!string.IsNullOrEmpty(secret) && secret.Length < MinSecretLength)
        {
            throw new InvalidOperationException($"Bff:SharedSecret must be at least {MinSecretLength} characters.");
        }
    }

    private static bool SecretsMatch(string provided, string expected) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(provided)),
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
}
