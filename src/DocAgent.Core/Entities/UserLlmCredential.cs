namespace DocAgent.Core.Entities;

public class UserLlmCredential
{
    public Guid UserId { get; set; }
    public LlmProvider Provider { get; set; }
    public required string Model { get; set; }

    /// <summary>Key encrypted with ASP.NET Core Data Protection (Phase 5). Never returned by the API.</summary>
    public required string EncryptedKey { get; set; }
    public required string KeyLast4 { get; set; }
    public DateTimeOffset ValidatedAt { get; set; }
}
