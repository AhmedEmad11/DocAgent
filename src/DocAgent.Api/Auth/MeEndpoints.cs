using DocAgent.Core.Entities;
using DocAgent.Infrastructure.Data;
using DocAgent.Infrastructure.Ingestion;
using DocAgent.Infrastructure.Llm;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DocAgent.Api.Auth;

public static class MeEndpoints
{
    public sealed record MeResponse(
        Guid Id, string? Email, bool EmailConfirmed, string[] ExternalLogins, bool HasLlmKey, DemoResponse? Demo, ChunkUsageResponse Chunks);

    /// <summary>Chunks stored across the user's ready documents against the account's total cap (SRS DOC-3); Limit 0 = no cap.</summary>
    public sealed record ChunkUsageResponse(int Used, int Limit);

    /// <summary>
    /// Present only while the user is served by the demo route (no saved key, demo configured): today's question allowance
    /// (UTC day) and the third-party notice (PRIV-1).
    /// </summary>
    public sealed record DemoResponse(int Limit, int Used, int Remaining, DateTimeOffset ResetsAt, string PrivacyNotice);

    public static void MapMe(this IEndpointRouteBuilder app)
    {
        var me = app.MapGroup("/api/v1/me").RequireAuthorization().WithTags("Account");

        var getMe = me.MapGet("", async (HttpContext http, UserManager<AppUser> users, AppDbContext db, INoKeyRouteProvider noKey, DemoQuotaService quota, IOptions<DocumentLimitsOptions> limitOptions, CancellationToken ct) =>
        {
            var user = await users.GetUserAsync(http.User);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            var logins = await users.GetLoginsAsync(user);
            var hasKey = await db.UserLlmCredentials.AnyAsync(ct); // tenant-filtered to this user
            DemoResponse? demo = null;
            if (!hasKey && noKey.IsDemo)
            {
                var status = await quota.GetStatusAsync(ct);
                demo = new DemoResponse(status.Limit, status.Used, status.Remaining, status.ResetsAt, DemoNoKeyRouteProvider.PrivacyNotice);
            }

            var used = await db.Documents.Where(d => d.Status == DocumentStatus.Ready).SumAsync(d => d.ChunkCount, ct); // tenant-filtered
            var limit = (hasKey ? limitOptions.Value.Byok : limitOptions.Value.Demo).MaxTotalChunks;

            return Results.Ok(new MeResponse(
                user.Id, user.Email, user.EmailConfirmed,
                logins.Select(l => l.LoginProvider).Order().ToArray(), hasKey, demo, new ChunkUsageResponse(used, limit)));
        });
        getMe.Produces<MeResponse>()
            .WithSummary("Get my account")
            .WithDescription("The signed-in user: id, email, whether the email is confirmed, linked external logins, whether an own LLM key is saved, chunk storage used against the account's total limit (chunks: used, limit), and in demo mode today's question allowance and the privacy notice. Works before the email is confirmed.");

        // AUTH-8: removing the user cascades (in the database) to documents, chunks, jobs, credential and codes.
        var deleteMe = me.MapDelete("", async (HttpContext http, UserManager<AppUser> users) =>
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
        deleteMe.Produces(StatusCodes.Status204NoContent)
            .WithSummary("Delete my account")
            .WithDescription("Permanently deletes the account with all documents, chunks, the stored LLM key and demo usage. Cannot be undone.");
    }
}
