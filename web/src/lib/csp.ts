// Content-Security-Policy builder (UI-SEC-4). Pure so it can be unit tested.
/**
 * `apiOrigin` is added to `connect-src` only for pages that upload straight to the API (plan A3, D40): the browser
 * posts the file there with a one-time ticket. Every other page can talk to the UI's own origin only.
 */
export function buildCsp(nonce: string, isDev: boolean, apiOrigin?: string): string {
  const directives: Record<string, string[]> = {
    "default-src": ["'self'"],
    // React dev tooling needs eval; production never gets it.
    "script-src": [
      "'self'",
      `'nonce-${nonce}'`,
      "'strict-dynamic'",
      ...(isDev ? ["'unsafe-eval'"] : []),
    ],
    "style-src": ["'self'", `'nonce-${nonce}'`],
    "img-src": ["'self'", "data:"],
    "font-src": ["'self'"],
    "connect-src": ["'self'", ...(apiOrigin ? [apiOrigin] : [])],
    "object-src": ["'none'"],
    "base-uri": ["'none'"],
    "form-action": ["'self'"],
    "frame-ancestors": ["'none'"],
  };
  return Object.entries(directives)
    .map(([k, v]) => `${k} ${v.join(" ")}`)
    .join("; ");
}

/** Pages that need the upload connection: the documents page in every locale. */
export const UPLOAD_PAGE = /^\/[a-z]{2}\/documents\/?$/;

export function apiOriginFor(pathname: string, apiUrl: string | undefined): string | undefined {
  if (!apiUrl || !UPLOAD_PAGE.test(pathname)) return undefined;
  try {
    return new URL(apiUrl).origin;
  } catch {
    return undefined;
  }
}
