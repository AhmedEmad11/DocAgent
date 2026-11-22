import { refresh as callRefresh, type TokenResponse } from "@/lib/api/auth";
import type { ApiRequestInit } from "@/lib/api/client";
import type { SessionData } from "./model";
import { singleFlight } from "./single-flight";

export type RefreshOutcome =
  | { kind: "ok"; session: SessionData }
  /** The API refused the refresh token (expired, malformed, user gone): the user must sign in again. */
  | { kind: "invalid" }
  /** API unreachable or failing: keep the session and try again later. */
  | { kind: "unavailable" };

export function toSessionData(tokens: TokenResponse, nowMs: number = Date.now()): SessionData {
  return {
    at: tokens.accessToken,
    rt: tokens.refreshToken,
    ate: Math.floor(nowMs / 1000) + tokens.expiresIn,
  };
}

// One in-flight refresh per refresh token and process, shared between the proxy and route handlers (they are separate bundles).
// Across serverless instances this cannot deduplicate; that is harmless because the API does not revoke the old refresh token (plan A8).
const store = ((
  globalThis as { __docagentRefresh?: Map<string, Promise<RefreshOutcome>> }
).__docagentRefresh ??= new Map());

// A browser fires several requests at once (page, RSC payloads, proxy calls) and all of them carry the same stale cookie until one
// response lands. For a few seconds after a refresh, requests presenting the same old refresh token reuse its result instead of
// refreshing again (UI-AUTH-8: concurrent calls share one refresh).
export const RECENT_TTL_MS = 10_000;
const recent = ((
  globalThis as { __docagentRecent?: Map<string, { at: number; session: SessionData }> }
).__docagentRecent ??= new Map());

export function refreshSession(
  session: SessionData,
  visitor: Pick<ApiRequestInit, "visitorHeaders">,
): Promise<RefreshOutcome> {
  const now = Date.now();
  for (const [key, entry] of recent) if (now - entry.at > RECENT_TTL_MS) recent.delete(key);
  const done = recent.get(session.rt);
  if (done) return Promise.resolve({ kind: "ok", session: done.session });

  return singleFlight(store, session.rt, async () => {
    const result = await callRefresh(visitor, session.rt);
    if (result.ok) {
      const fresh = toSessionData(result.data);
      recent.set(session.rt, { at: Date.now(), session: fresh });
      return { kind: "ok", session: fresh };
    }
    const { status } = result.problem;
    return status === 400 || status === 401 || status === 403
      ? { kind: "invalid" }
      : { kind: "unavailable" };
  });
}
