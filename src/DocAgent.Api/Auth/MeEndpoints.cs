using DocAgent.Core.Entities;
using DocAgent.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace DocAgent.Api.Auth;

public static class MeEndpoints
{
    public sealed record MeResponse(
        Guid Id, string? Email, bool EmailConfirmed, string[] ExternalLogins, bool HasLlmKey);

    public static void MapMe(this IEndpointRouteBuilder app)
    {
        var me = app.MapGroup("/api/v1/me").RequireAuthorization();

        me.MapGet("", async (HttpContext http, UserManager<AppUser> users, AppDbContext db, CancellationToken ct) =>
        {
            var user = await users.GetUserAsync(http.User);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            var logins = await users.GetLoginsAsync(user);
            var hasKey = await db.UserLlmCredentials.AnyAsync(ct); // tenant-filtered to this user
            return Results.Ok(new MeResponse(
                user.Id, user.Email, user.EmailConfirmed,
                logins.Select(l => l.LoginProvider).Order().ToArray(), hasKey));
        });

        // AUTH-8: removing the user cascades (in the database) to documents, chunks, jobs, credential and codes.
        me.MapDelete("", async (HttpContext http, UserManager<AppUser> users) =>
        {
            var user = await users.GetUserAsync(http.User);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            var result = await users.DeleteAsync(user);
            return result.Succeeded
                ? Results.NoContent()
                : Results.Problem("The account could not be deleted.", statusCode: StatusCodes.Status500InternalServerError);
        });
    }
}
