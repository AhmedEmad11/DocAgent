import { EncryptJWT, jwtDecrypt } from "jose";

// Encrypted, authenticated tokens for cookies (UI-SEC-1): JWE `dir` + AES-256-GCM, key derived from the
// secret with SHA-256 and a purpose label. Pure and runtime-agnostic (Node and Edge), so it is unit tested.
// Why jose and not iron-session: GCM as the spec names it, a smaller cookie (one base64 step instead of two),
// and the same primitive for the session and the short-lived external-login state cookie. See docs/DEVELOPMENT_PLAN.md 9.1.

export interface Secrets {
  current: string;
  previous?: string | undefined;
}

export type SealPurpose = "session" | "external-state" | "pending-email";

async function deriveKey(secret: string, purpose: SealPurpose): Promise<Uint8Array> {
  const bytes = new TextEncoder().encode(`docagent/${purpose}/v1\0${secret}`);
  return new Uint8Array(await crypto.subtle.digest("SHA-256", bytes));
}

export async function seal(
  payload: Record<string, unknown>,
  purpose: SealPurpose,
  secrets: Secrets,
  ttlSeconds: number,
  nowMs: number = Date.now(),
): Promise<string> {
  const key = await deriveKey(secrets.current, purpose);
  const iat = Math.floor(nowMs / 1000);
  return new EncryptJWT(payload)
    .setProtectedHeader({ alg: "dir", enc: "A256GCM" })
    .setAudience(purpose)
    .setIssuedAt(iat)
    .setExpirationTime(iat + ttlSeconds)
    .encrypt(key);
}

/** Returns the payload, or null for anything that is not a valid, unexpired token for this purpose. Tries the previous secret too (rotation). */
export async function unseal(
  token: string,
  purpose: SealPurpose,
  secrets: Secrets,
  nowMs: number = Date.now(),
): Promise<Record<string, unknown> | null> {
  for (const secret of [secrets.current, secrets.previous]) {
    if (!secret) continue;
    try {
      const { payload } = await jwtDecrypt(token, await deriveKey(secret, purpose), {
        audience: purpose,
        keyManagementAlgorithms: ["dir"],
        contentEncryptionAlgorithms: ["A256GCM"],
        currentDate: new Date(nowMs),
      });
      return payload;
    } catch {
      // try the next secret
    }
  }
  return null;
}
