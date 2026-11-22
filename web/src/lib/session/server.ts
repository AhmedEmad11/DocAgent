import "server-only";
import { cookies, headers } from "next/headers";
import { apiFetch, type ApiRequestInit } from "@/lib/api/client";
import { getServerEnv } from "@/lib/env";
import { cookieOptions, sessionCookieName } from "./cookie";
import {
  needsRefresh,
  SESSION_MAX_AGE_SECONDS,
  sealSessionData,
  unsealSessionData,
  type SessionData,
} from "./model";
import { refreshSession } from "./refresh";
import type { Secrets } from "./seal";

export function sessionSecrets(): Secrets {
  const env = getServerEnv();
  return { current: env.SESSION_SECRET, previous: env.SESSION_SECRET_PREVIOUS };
}

/** Reads and decrypts the session cookie. Never refreshes (server components cannot set cookies; the proxy does that). */
export async function readSession(): Promise<SessionData | null> {
  const jar = await cookies();
  const raw = jar.get(sessionCookieName(getServerEnv().NEXT_PUBLIC_SITE_URL))?.value;
  return raw ? unsealSessionData(raw, sessionSecrets()) : null;
}

/** Only from route handlers and server actions (the only places Next allows setting cookies). */
export async function writeSession(data: SessionData): Promise<void> {
  const siteUrl = getServerEnv().NEXT_PUBLIC_SITE_URL;
  const jar = await cookies();
  jar.set(
    sessionCookieName(siteUrl),
    await sealSessionData(data, sessionSecrets()),
    cookieOptions(siteUrl, SESSION_MAX_AGE_SECONDS),
  );
}

export async function clearSession(): Promise<void> {
  const jar = await cookies();
  jar.delete(sessionCookieName(getServerEnv().NEXT_PUBLIC_SITE_URL));
}

export type AuthedResult =
  { kind: "anonymous" } | { kind: "network" } | { kind: "response"; res: Response };

/**
 * Calls the API with the session's access token (UI-AUTH-8): refreshes first when it is about to expire,
 * and once more and retries once on a 401. A failed refresh clears the session. Route handlers and server actions only.
 */
export async function authedApiFetch(
  path: string,
  init: Omit<ApiRequestInit, "accessToken" | "visitorHeaders"> = {},
): Promise<AuthedResult> {
  const visitor = { visitorHeaders: await headers() };
  let session = await readSession();
  if (!session) return { kind: "anonymous" };

  let refreshed = false;
  if (needsRefresh(session)) {
    const outcome = await refreshSession(session, visitor);
    if (outcome.kind === "invalid") {
      await clearSession();
      return { kind: "anonymous" };
    }
    if (outcome.kind === "ok") {
      session = outcome.session;
      refreshed = true;
      await writeSession(session);
    }
  }

  let res = await apiFetch(path, { ...init, ...visitor, accessToken: session.at });
  if (res?.status === 401 && !refreshed) {
    await res.body?.cancel();
    const outcome = await refreshSession(session, visitor);
    if (outcome.kind === "invalid") {
      await clearSession();
      return { kind: "anonymous" };
    }
    if (outcome.kind === "ok") {
      session = outcome.session;
      await writeSession(session);
      res = await apiFetch(path, { ...init, ...visitor, accessToken: session.at });
    }
  }
  if (!res) return { kind: "network" };
  if (res.status === 401) {
    // Still unauthorised after a refresh: the account or its tokens are gone.
    await res.body?.cancel();
    await clearSession();
    return { kind: "anonymous" };
  }
  return { kind: "response", res };
}
