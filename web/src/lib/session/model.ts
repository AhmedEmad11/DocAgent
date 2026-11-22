import { z } from "zod";
import { seal, unseal, type Secrets } from "./seal";

// What the sealed session cookie holds (UI-SEC-1). Nothing else is stored: no email, no profile.
export interface SessionData {
  /** API access token. */
  at: string;
  /** API refresh token. */
  rt: string;
  /** Access token expiry, epoch seconds. */
  ate: number;
}

const sessionSchema = z.object({ at: z.string().min(1), rt: z.string().min(1), ate: z.number() });

/** Absolute session lifetime = the refresh token's lifetime (D26, UI-SEC-2); renewed whenever the tokens are refreshed. */
export const SESSION_MAX_AGE_SECONDS = 14 * 24 * 60 * 60;
/** Refresh this long before the access token expires. */
export const REFRESH_MARGIN_SECONDS = 60;
/** Browsers keep at most about 4 KB per cookie (name + attributes + value); stay clear of it (UI-SEC-2). */
export const MAX_COOKIE_VALUE_BYTES = 3800;

export async function sealSessionData(
  data: SessionData,
  secrets: Secrets,
  nowMs?: number,
): Promise<string> {
  const token = await seal({ ...data }, "session", secrets, SESSION_MAX_AGE_SECONDS, nowMs);
  if (token.length > MAX_COOKIE_VALUE_BYTES) {
    // Fail loudly instead of letting the browser drop the cookie silently (chunking is added if this ever happens, plan A8).
    throw new Error("Sealed session is too large for one cookie");
  }
  return token;
}

export async function unsealSessionData(
  token: string,
  secrets: Secrets,
  nowMs?: number,
): Promise<SessionData | null> {
  const payload = await unseal(token, "session", secrets, nowMs);
  const parsed = sessionSchema.safeParse(payload);
  return parsed.success ? parsed.data : null;
}

export function needsRefresh(data: SessionData, nowMs: number = Date.now()): boolean {
  return data.ate - REFRESH_MARGIN_SECONDS <= Math.floor(nowMs / 1000);
}
