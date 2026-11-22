/**
 * CSRF check for state-changing BFF routes and server actions (UI-SEC-3). `Origin` must equal the site's own
 * origin; when the browser sends no `Origin`, `Sec-Fetch-Site: same-origin` is accepted. Anything else is rejected.
 */
export function isSameOrigin(
  headers: { get(name: string): string | null },
  siteUrl: string,
): boolean {
  const site = new URL(siteUrl).origin;
  const origin = headers.get("origin");
  if (origin !== null) return origin === site;
  return headers.get("sec-fetch-site") === "same-origin";
}
