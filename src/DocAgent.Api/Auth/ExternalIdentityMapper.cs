using System.Security.Claims;
using DocAgent.Infrastructure.Auth;
using Microsoft.AspNetCore.Identity;

namespace DocAgent.Api.Auth;

/// <summary>Turns a provider's claims into an <see cref="ExternalIdentity"/> with a trustworthy "verified" flag.</summary>
public static class ExternalIdentityMapper
{
    /// <summary>Claim added for GitHub: the primary email, only when GitHub reports it as verified.</summary>
    public const string GitHubVerifiedEmailClaim = "docagent:github_verified_email";

    /// <summary>Claim added for Google from the userinfo "email_verified" field.</summary>
    public const string GoogleEmailVerifiedClaim = "email_verified";

    public static ExternalIdentity? From(ExternalLoginInfo info) =>
        From(info.LoginProvider, info.ProviderKey, info.Principal);

    public static ExternalIdentity From(string provider, string providerKey, ClaimsPrincipal principal)
    {
        switch (provider)
        {
            case "Google":
                var email = principal.FindFirstValue(ClaimTypes.Email);
                var verified = string.Equals(
                    principal.FindFirstValue(GoogleEmailVerifiedClaim), "true", StringComparison.OrdinalIgnoreCase);
                return new ExternalIdentity(provider, providerKey, email, verified);

            case "GitHub":
                // Deliberately ignore the plain email claim: GitHub's handler does not check verification.
                var ghEmail = principal.FindFirstValue(GitHubVerifiedEmailClaim);
                return new ExternalIdentity(provider, providerKey, ghEmail, !string.IsNullOrWhiteSpace(ghEmail));

            default:
                return new ExternalIdentity(provider, providerKey, null, false);
        }
    }
}
