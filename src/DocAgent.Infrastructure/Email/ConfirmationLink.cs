using System.Net;
using Microsoft.AspNetCore.WebUtilities;

namespace DocAgent.Infrastructure.Email;

/// <summary>
/// Plan A5 / OI-9: when <c>Email:ConfirmationUrlBase</c> is set, the emailed confirmation link opens the UI page instead of the API
/// endpoint. The UI calls <c>GET /auth/confirmEmail?userId=&amp;code=</c> itself (through its backend-for-frontend).
/// </summary>
public static class ConfirmationLink
{
    /// <summary>True when <paramref name="urlBase"/> is an absolute http(s) URL without a fragment.</summary>
    public static bool IsValidBase(string? urlBase) =>
        Uri.TryCreate(urlBase, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && uri.Fragment.Length == 0;

    /// <summary>
    /// Builds <c>{urlBase}?userId=..&amp;code=..</c> from the API link Identity generated (HTML-encoded or not).
    /// Returns the original link (decoded) when no valid base is configured or the link lacks <c>userId</c> or <c>code</c>.
    /// <c>changedEmail</c> is carried along when present (not used by v1 flows).
    /// </summary>
    public static string Rewrite(string identityLink, string? urlBase)
    {
        var plain = WebUtility.HtmlDecode(identityLink);
        if (!IsValidBase(urlBase) || !Uri.TryCreate(plain, UriKind.Absolute, out var original))
        {
            return plain;
        }

        var query = QueryHelpers.ParseQuery(original.Query);
        if (!query.TryGetValue("userId", out var userId) || !query.TryGetValue("code", out var code)
            || userId.Count != 1 || code.Count != 1)
        {
            return plain;
        }

        var link = QueryHelpers.AddQueryString(urlBase!, "userId", userId.ToString());
        link = QueryHelpers.AddQueryString(link, "code", code.ToString());
        if (query.TryGetValue("changedEmail", out var changed) && changed.Count == 1)
        {
            link = QueryHelpers.AddQueryString(link, "changedEmail", changed.ToString());
        }

        return link;
    }
}
