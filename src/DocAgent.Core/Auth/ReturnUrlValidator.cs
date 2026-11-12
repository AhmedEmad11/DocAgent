namespace DocAgent.Core.Auth;

/// <summary>
/// Validates the client return URL of an external sign-in against an allowlist of absolute URL
/// prefixes: scheme, host and port must match exactly; the path must match on a segment boundary.
/// </summary>
public static class ReturnUrlValidator
{
    public static bool TryValidate(string? returnUrl, IEnumerable<string> allowedPrefixes, out string? validated)
    {
        validated = null;
        if (string.IsNullOrWhiteSpace(returnUrl)
            || !Uri.TryCreate(returnUrl, UriKind.Absolute, out var url)
            || !IsHttp(url)
            || url.UserInfo.Length > 0
            || url.Fragment.Length > 0)
        {
            return false;
        }

        foreach (var prefix in allowedPrefixes)
        {
            if (!Uri.TryCreate(prefix, UriKind.Absolute, out var allowed) || !IsHttp(allowed))
            {
                continue;
            }

            if (string.Equals(url.Scheme, allowed.Scheme, StringComparison.OrdinalIgnoreCase)
                && string.Equals(url.IdnHost, allowed.IdnHost, StringComparison.OrdinalIgnoreCase)
                && url.Port == allowed.Port
                && PathMatches(url.AbsolutePath, allowed.AbsolutePath))
            {
                validated = url.AbsoluteUri;
                return true;
            }
        }

        return false;
    }

    private static bool IsHttp(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;

    private static bool PathMatches(string path, string prefix)
    {
        prefix = prefix.TrimEnd('/');
        if (prefix.Length == 0)
        {
            return true;
        }

        return path.StartsWith(prefix, StringComparison.Ordinal)
            && (path.Length == prefix.Length || path[prefix.Length] == '/');
    }
}
