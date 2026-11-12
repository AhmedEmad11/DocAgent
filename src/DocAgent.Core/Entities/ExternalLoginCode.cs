namespace DocAgent.Core.Entities;

/// <summary>One-time code handed to the client after external sign-in (SRS D24). Only a hash is stored.</summary>
public class ExternalLoginCode
{
    public Guid Id { get; set; }

    /// <summary>SHA-256 hex of the code. The raw code is never stored.</summary>
    public required string CodeHash { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
