using DocAgent.Core.Entities;
using Microsoft.AspNetCore.Identity;

namespace DocAgent.Infrastructure.Auth;

/// <summary>What the external provider told us about the signed-in person.</summary>
/// <param name="Provider">Login provider name, e.g. "Google" or "GitHub".</param>
/// <param name="ProviderKey">The provider's stable user id.</param>
/// <param name="Email">Email reported by the provider (may be null).</param>
/// <param name="EmailVerified">Whether the provider verified that email.</param>
public sealed record ExternalIdentity(string Provider, string ProviderKey, string? Email, bool EmailVerified);

public enum ExternalAccountError { None, EmailNotVerified, NoVerifiedEmail, Failed }

public sealed record ExternalAccountResult(AppUser? User, ExternalAccountError Error)
{
    public bool Succeeded => User is not null;
    public static ExternalAccountResult Ok(AppUser user) => new(user, ExternalAccountError.None);
    public static ExternalAccountResult Fail(ExternalAccountError error) => new(null, error);
}

/// <summary>Maps an external identity to a local user (SRS AUTH-3/4/5, D21).</summary>
public sealed class ExternalAccountResolver(UserManager<AppUser> users)
{
    public async Task<ExternalAccountResult> ResolveAsync(ExternalIdentity identity)
    {
        // 1. Already linked.
        var linked = await users.FindByLoginAsync(identity.Provider, identity.ProviderKey);
        if (linked is not null)
        {
            return ExternalAccountResult.Ok(linked);
        }

        if (string.IsNullOrWhiteSpace(identity.Email))
        {
            return ExternalAccountResult.Fail(ExternalAccountError.NoVerifiedEmail);
        }

        var existing = await users.FindByEmailAsync(identity.Email);

        // 3. Unverified email: never link to (or create from) an address the provider did not verify.
        if (!identity.EmailVerified)
        {
            return ExternalAccountResult.Fail(
                existing is not null ? ExternalAccountError.EmailNotVerified : ExternalAccountError.NoVerifiedEmail);
        }

        var loginInfo = new UserLoginInfo(identity.Provider, identity.ProviderKey, identity.Provider);

        // 2. Verified email matches an existing account: link and confirm the email.
        if (existing is not null)
        {
            var added = await users.AddLoginAsync(existing, loginInfo);
            if (!added.Succeeded)
            {
                return ExternalAccountResult.Fail(ExternalAccountError.Failed);
            }

            if (!existing.EmailConfirmed)
            {
                // The email was never proven by whoever registered it, so a password they set must not
                // survive: otherwise someone could pre-register a victim's address with a password
                // and keep access after the victim signs in with Google/GitHub.
                if (await users.HasPasswordAsync(existing))
                {
                    await users.RemovePasswordAsync(existing);
                }

                existing.EmailConfirmed = true;
                var updated = await users.UpdateAsync(existing);
                if (!updated.Succeeded)
                {
                    return ExternalAccountResult.Fail(ExternalAccountError.Failed);
                }
            }

            return ExternalAccountResult.Ok(existing);
        }

        // 4. New user.
        var user = new AppUser { UserName = identity.Email, Email = identity.Email, EmailConfirmed = true };
        var created = await users.CreateAsync(user);
        if (!created.Succeeded)
        {
            return ExternalAccountResult.Fail(ExternalAccountError.Failed);
        }

        var linkedNew = await users.AddLoginAsync(user, loginInfo);
        if (!linkedNew.Succeeded)
        {
            await users.DeleteAsync(user);
            return ExternalAccountResult.Fail(ExternalAccountError.Failed);
        }

        return ExternalAccountResult.Ok(user);
    }
}
