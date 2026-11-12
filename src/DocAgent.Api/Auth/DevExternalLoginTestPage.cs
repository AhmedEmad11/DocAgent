namespace DocAgent.Api.Auth;

/// <summary>Development-only page to try external sign-in end to end (SRS D24 "built-in API test page").</summary>
public static class DevExternalLoginTestPage
{
    public const string Path = "/dev/external-login-test";

    public static void MapDevExternalLoginTestPage(this IEndpointRouteBuilder app) =>
        app.MapGet(Path, () => Results.Content(Html, "text/html; charset=utf-8")).ExcludeFromDescription();

    private const string Html = """
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>DocAgent external login test</title>
          <style>
            body { font-family: system-ui, sans-serif; max-width: 40rem; margin: 2rem auto; padding: 0 1rem; }
            a.btn { display: inline-block; margin-right: .75rem; padding: .5rem 1rem; border: 1px solid #888; border-radius: 6px; text-decoration: none; color: inherit; }
            pre { background: #8881; padding: 1rem; overflow-x: auto; }
          </style>
        </head>
        <body>
          <h1>External login test</h1>
          <p>Development only. Tokens are masked below.</p>
          <p>
            <a class="btn" id="google" href="#">Sign in with Google</a>
            <a class="btn" id="github" href="#">Sign in with GitHub</a>
          </p>
          <pre id="out">Not signed in.</pre>
          <script>
            const here = location.origin + location.pathname;
            const q = '?returnUrl=' + encodeURIComponent(here);
            document.getElementById('google').href = '/api/v1/auth/external/google' + q;
            document.getElementById('github').href = '/api/v1/auth/external/github' + q;

            const mask = v => typeof v === 'string' && v.length > 8 ? v.slice(0, 4) + '...' + v.slice(-4) : v;
            const out = document.getElementById('out');
            const params = new URLSearchParams(location.search);
            const code = params.get('code');
            const error = params.get('error');
            history.replaceState(null, '', location.pathname); // drop the one-time code from the address bar

            if (error) {
              out.textContent = 'Sign-in failed: ' + error;
            } else if (code) {
              fetch('/api/v1/auth/external/exchange', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ code })
              })
                .then(async r => ({ status: r.status, body: await r.json().catch(() => null) }))
                .then(({ status, body }) => {
                  if (body) for (const k of ['accessToken', 'refreshToken']) if (k in body) body[k] = mask(body[k]);
                  out.textContent = 'HTTP ' + status + '\n' + JSON.stringify(body, null, 2);
                })
                .catch(e => { out.textContent = 'Request failed: ' + e; });
            }
          </script>
        </body>
        </html>
        """;
}
