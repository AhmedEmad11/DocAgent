using DocAgent.Core.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;

namespace DocAgent.Api.Auth;

public sealed class EmailConfirmedRequirement : IAuthorizationRequirement;

/// <summary>
/// Succeeds only if the user exists and has EmailConfirmed = true in the database
/// (not merely a claim, which can be stale for the lifetime of a token).
/// </summary>
public sealed class EmailConfirmedHandler(UserManager<AppUser> users)
    : AuthorizationHandler<EmailConfirmedRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, EmailConfirmedRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return;
        }

        var user = await users.GetUserAsync(context.User);
        if (user is { EmailConfirmed: true })
        {
            context.Succeed(requirement);
        }
    }
}
