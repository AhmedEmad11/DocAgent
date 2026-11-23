using System.Security.Claims;
using DocAgent.Core.Auth;

namespace DocAgent.Api.Security;

/// <summary>
/// Plan A3 / D40: authenticates <c>POST /api/v1/documents</c> from an <c>X-Upload-Ticket</c> header when no bearer token is sent. The ticket is
/// redeemed (used up) here and turned into a principal that carries only the user id; the endpoint's normal policy (email confirmed, checked
/// in the database), the per-user upload cap and the full document limits then apply exactly as for a bearer call. The header is ignored on
/// every other route, so a ticket cannot be used for anything else.
/// </summary>
public static class UploadTicketAuthentication
{
    public const string AuthenticationType = "UploadTicket";

    public static IApplicationBuilder UseUploadTicketAuthentication(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var request = context.Request;
        if (HttpMethods.IsPost(request.Method)
            && request.Path.Equals(HttpHardening.UploadPath, StringComparison.OrdinalIgnoreCase)
            && !request.Headers.ContainsKey("Authorization")
            && request.Headers.TryGetValue(UploadTicketService.Header, out var header))
        {
            var userId = context.RequestServices.GetRequiredService<UploadTicketService>().Redeem(header.ToString());
            if (userId is { } id)
            {
                context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id.ToString())], AuthenticationType));
            }
        }

        await next();
    });
}
