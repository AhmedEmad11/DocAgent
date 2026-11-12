using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace DocAgent.Infrastructure.Llm;

/// <summary>Encrypts saved API keys (SRS SEC-1) with ASP.NET Core Data Protection.</summary>
public interface ILlmKeyProtector
{
    string Protect(Guid userId, string apiKey);

    /// <returns>The key, or null when the payload cannot be decrypted (other user, key ring changed, damaged).</returns>
    string? Unprotect(Guid userId, string payload);
}

/// <summary>
/// Purpose string <c>DocAgent.UserLlmKey.v1</c> is used for keys only, with a sub-purpose per user id: a ciphertext copied
/// to another user's row does not decrypt. Key ring location and persistence are a deployment matter (Phase 8):
/// in development the default per-user folder is used; a container needs a persistent, separately stored ring (SEC-1).
/// </summary>
public sealed class LlmKeyProtector(IDataProtectionProvider provider) : ILlmKeyProtector
{
    public const string Purpose = "DocAgent.UserLlmKey.v1";

    public string Protect(Guid userId, string apiKey) => For(userId).Protect(apiKey);

    public string? Unprotect(Guid userId, string payload)
    {
        try
        {
            return For(userId).Unprotect(payload);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private IDataProtector For(Guid userId) => provider.CreateProtector(Purpose, userId.ToString("N"));
}
