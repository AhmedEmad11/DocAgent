namespace DocAgent.Core.Abstractions;

/// <summary>The authenticated caller. Drives tenant isolation (SRS SEC-3).</summary>
public interface ICurrentUser
{
    /// <summary>Id of the authenticated user, or null when the request is anonymous.</summary>
    Guid? UserId { get; }
}
