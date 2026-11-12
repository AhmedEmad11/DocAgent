namespace DocAgent.Core.Entities;

public class UserLlmCredential
{
    public Guid UserId { get; set; }
    public LlmProvider Provider { get; set; }
    public required string Model { get; set; }

    /// <summary>Key encrypted with ASP.NET Core Data Protection (purpose <c>DocAgent.UserLlmKey.v1</c>, bound to the user id). Never returned by the API.</summary>
    public required string EncryptedKey { get; set; }
    public required string KeyLast4 { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Last time the key or model was saved (every save validates the key first, SRS KEY-2).</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
