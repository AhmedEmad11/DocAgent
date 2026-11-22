// Cookie names and flags (UI-SEC-2). Pure: takes the public site URL so it can be tested for http and https.
export function isSecureSite(siteUrl: string): boolean {
  return new URL(siteUrl).protocol === "https:";
}

/** `__Host-` needs Secure, Path=/ and no Domain, so it is used whenever the site is served over HTTPS (production). */
export function sessionCookieName(siteUrl: string): string {
  return isSecureSite(siteUrl) ? "__Host-docagent_session" : "docagent_session";
}

export function externalStateCookieName(siteUrl: string): string {
  return isSecureSite(siteUrl) ? "__Host-docagent_ext" : "docagent_ext";
}

export interface CookieOptions {
  httpOnly: true;
  secure: boolean;
  sameSite: "lax";
  path: "/";
  maxAge: number;
}

/** HttpOnly, Secure on HTTPS, SameSite=Lax (the external callback is a cross-site top-level GET, so Strict would drop the nonce), Path=/, no Domain. */
export function cookieOptions(siteUrl: string, maxAgeSeconds: number): CookieOptions {
  return {
    httpOnly: true,
    secure: isSecureSite(siteUrl),
    sameSite: "lax",
    path: "/",
    maxAge: maxAgeSeconds,
  };
}

export function pendingEmailCookieName(siteUrl: string): string {
  return isSecureSite(siteUrl) ? "__Host-docagent_pending" : "docagent_pending";
}
