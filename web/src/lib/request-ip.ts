import { isIP } from "node:net";

/**
 * The visitor's address for the API's per-IP auth limit (D41). On Vercel `x-forwarded-for` is set by the platform
 * (the left-most entry is the client). Returns undefined when there is no valid address (local development).
 */
export function getClientIp(headers: { get(name: string): string | null }): string | undefined {
  const candidates = [
    headers.get("x-vercel-forwarded-for"),
    headers.get("x-forwarded-for"),
    headers.get("x-real-ip"),
  ];
  for (const value of candidates) {
    const first = value?.split(",")[0]?.trim();
    if (first && isIP(first) !== 0) return first;
  }
  return undefined;
}
