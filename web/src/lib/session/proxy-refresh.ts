import { getServerEnv } from "@/lib/env";
import { sessionCookieName } from "./cookie";
import { needsRefresh, sealSessionData, unsealSessionData } from "./model";
import { refreshSession } from "./refresh";

export type ProxyRefresh =
  | { action: "none" }
  | { action: "set"; name: string; value: string }
  | { action: "clear"; name: string };

/**
 * Runs in the proxy before pages render (UI-AUTH-8): when the access token is about to expire, refresh it and hand the new
 * sealed cookie both to this request (so server components see it) and to the response. A refresh token the API refuses clears
 * the cookie; an unreachable API leaves it alone.
 */
export async function refreshForRequest(request: {
  headers: { get(name: string): string | null };
  cookies: { get(name: string): { value: string } | undefined };
}): Promise<ProxyRefresh> {
  const name = sessionCookieName(process.env.NEXT_PUBLIC_SITE_URL ?? "http://localhost");
  const raw = request.cookies.get(name)?.value;
  if (!raw) return { action: "none" };

  const env = getServerEnv();
  const secrets = { current: env.SESSION_SECRET, previous: env.SESSION_SECRET_PREVIOUS };
  const session = await unsealSessionData(raw, secrets);
  if (!session) return { action: "clear", name }; // tampered, expired or sealed with a retired secret
  if (!needsRefresh(session)) return { action: "none" };

  const outcome = await refreshSession(session, { visitorHeaders: request.headers });
  if (outcome.kind === "ok") {
    return { action: "set", name, value: await sealSessionData(outcome.session, secrets) };
  }
  return outcome.kind === "invalid" ? { action: "clear", name } : { action: "none" };
}
