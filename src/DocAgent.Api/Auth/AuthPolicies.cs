namespace DocAgent.Api.Auth;

public static class AuthPolicies
{
    /// <summary>Requires the user's email to be confirmed (checked against the database). SRS AUTH-7.</summary>
    public const string EmailConfirmed = "EmailConfirmed";
}
