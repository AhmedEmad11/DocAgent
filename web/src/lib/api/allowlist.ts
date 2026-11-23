// The only API routes the generic BFF proxy forwards (UI-SEC-7). The target URL is built from the matched path;
// no user-supplied URL or host reaches fetch. Not here on purpose: the auth endpoints (own server actions), the
// upload (ticket flow, 9.2) and the ask stream (own relay route, 9.3).
export interface AllowedRoute {
  method: "GET" | "POST" | "PUT" | "DELETE";
  /** Matches the path after `/api/v1`. */
  pattern: RegExp;
  /** Browser request headers that may be forwarded to the API for this route (lower case). */
  headers?: readonly string[];
  /** Largest request body accepted before forwarding, in bytes. */
  maxBodyBytes: number;
  /** Upstream timeout in ms when the default (cold start, 60 s) is too short: whole-document workflows run up to the agent timeout (D20). */
  timeoutMs?: number;
}

const ID = "[0-9a-fA-F-]{36}";
const SMALL = 16 * 1024;
/** Above the 120 s BYOK agent timeout (D20) plus margin; the proxy route sets `maxDuration` higher (UI-PERF-4). */
export const LONG_CALL_TIMEOUT_MS = 170_000;

export const ALLOWED_ROUTES: readonly AllowedRoute[] = [
  { method: "GET", pattern: /^\/me$/, maxBodyBytes: 0 },
  { method: "DELETE", pattern: /^\/me$/, maxBodyBytes: 0 },
  { method: "GET", pattern: /^\/me\/llm-key$/, maxBodyBytes: 0 },
  { method: "PUT", pattern: /^\/me\/llm-key$/, headers: ["content-type"], maxBodyBytes: SMALL },
  { method: "DELETE", pattern: /^\/me\/llm-key$/, maxBodyBytes: 0 },
  {
    method: "GET",
    pattern: /^\/providers\/[a-z0-9-]{1,32}\/models$/,
    headers: ["x-llm-api-key"],
    maxBodyBytes: 0,
  },
  { method: "GET", pattern: /^\/documents$/, maxBodyBytes: 0 },
  { method: "GET", pattern: new RegExp(`^/documents/${ID}$`), maxBodyBytes: 0 },
  { method: "DELETE", pattern: new RegExp(`^/documents/${ID}$`), maxBodyBytes: 0 },
  {
    method: "POST",
    pattern: new RegExp(`^/documents/${ID}/summary$`),
    headers: ["content-type"],
    maxBodyBytes: SMALL,
    timeoutMs: LONG_CALL_TIMEOUT_MS,
  },
  {
    method: "POST",
    pattern: /^\/documents\/compare$/,
    headers: ["content-type"],
    maxBodyBytes: SMALL,
    timeoutMs: LONG_CALL_TIMEOUT_MS,
  },
  {
    method: "POST",
    pattern: /^\/ask$/,
    headers: ["content-type"],
    maxBodyBytes: 64 * 1024,
    timeoutMs: LONG_CALL_TIMEOUT_MS,
  },
];

const SEGMENT = /^[A-Za-z0-9._-]{1,64}$/;

/** Returns the matched route and the normalised API path, or null when the call is not allowed. */
export function matchAllowed(
  method: string,
  segments: readonly string[],
): { route: AllowedRoute; path: string } | null {
  if (segments.length === 0 || segments.length > 4) return null;
  if (!segments.every((s) => SEGMENT.test(s) && s !== "." && s !== "..")) return null;
  const path = `/${segments.join("/")}`;
  const route = ALLOWED_ROUTES.find((r) => r.method === method && r.pattern.test(path));
  return route ? { route, path } : null;
}
