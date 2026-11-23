import { getServerEnv } from "@/lib/env";
import { getClientIp } from "@/lib/request-ip";
import {
  API_STARTING_PROBLEM,
  isColdStartResponse,
  parseProblem,
  type ApiProblem,
} from "./problem";

export type ApiResult<T> =
  { ok: true; status: number; data: T } | { ok: false; problem: ApiProblem };

export interface ApiRequestInit {
  method?: "GET" | "POST" | "PUT" | "PATCH" | "DELETE";
  /** JSON body. */
  json?: unknown;
  /** Raw body (proxy). */
  body?: BodyInit;
  headers?: Record<string, string>;
  accessToken?: string;
  /** Request headers of the visitor, used for the forwarded client IP. */
  visitorHeaders?: { get(name: string): string | null };
  signal?: AbortSignal;
  timeoutMs?: number;
}

/** Cold starts on SnapDeploy take up to about a minute (UI-GEN-7). */
export const DEFAULT_TIMEOUT_MS = 60_000;

export const fetchImpl = { current: (input: string, init: RequestInit) => fetch(input, init) };

/** Builds `{DOCAGENT_API_URL}/api/v1{path}`; path is fixed by our own code or the proxy allowlist, never a user-supplied URL. */
export function apiUrl(path: string): string {
  return `${getServerEnv().DOCAGENT_API_URL}/api/v1${path}`;
}

/** Low-level call. Returns the raw Response (caller must consume it) or null when the API cannot be reached. */
export async function apiFetch(path: string, init: ApiRequestInit = {}): Promise<Response | null> {
  const env = getServerEnv();
  const headers: Record<string, string> = { accept: "application/json", ...init.headers };
  if (init.json !== undefined) headers["content-type"] = "application/json";
  if (init.accessToken) headers.authorization = `Bearer ${init.accessToken}`;
  // D41 / plan A4: the visitor's address, trusted by the API only together with the shared secret.
  const ip = init.visitorHeaders ? getClientIp(init.visitorHeaders) : undefined;
  if (ip) {
    headers["x-client-ip"] = ip;
    headers["x-bff-secret"] = env.BFF_SHARED_SECRET;
  }
  const timeout = AbortSignal.timeout(init.timeoutMs ?? DEFAULT_TIMEOUT_MS);
  try {
    const res = await fetchImpl.current(apiUrl(path), {
      method: init.method ?? "GET",
      headers,
      body: init.json !== undefined ? JSON.stringify(init.json) : init.body,
      signal: init.signal ? AbortSignal.any([init.signal, timeout]) : timeout,
      redirect: "manual",
      cache: "no-store",
    });
    if (isColdStartResponse(res)) triggerWake();
    return res;
  } catch {
    // No answer (connection refused or timeout): treated as a cold start (UI-GEN-7).
    triggerWake();
    return null;
  }
}

const WAKE_MIN_INTERVAL_MS = 20_000;
const WAKE_TIMEOUT_MS = 5_000;
let lastWakeAt = 0;

/** For tests. */
export function resetWakeThrottle(): void {
  lastWakeAt = 0;
}

/**
 * Wakes a sleeping API host (UI-GEN-7): the host's sleep page only wakes through a browser POST, so a server-side request never does.
 * Fire and forget, at most once per 20 s per server instance, no body and no headers of the visitor, never throws. No URL set: no call.
 */
export function triggerWake(): void {
  const url = getServerEnv().DOCAGENT_API_WAKE_URL;
  if (!url) return;
  const now = Date.now();
  if (lastWakeAt !== 0 && now - lastWakeAt < WAKE_MIN_INTERVAL_MS) return;
  lastWakeAt = now;
  void Promise.resolve()
    .then(() =>
      fetchImpl.current(url, {
        method: "POST",
        signal: AbortSignal.timeout(WAKE_TIMEOUT_MS),
        redirect: "manual",
        cache: "no-store",
      }),
    )
    .then((r) => r.body?.cancel())
    .catch(() => undefined);
}

/** JSON call with ProblemDetails parsing. `parse` validates and narrows the 2xx body. */
export async function apiJson<T>(
  path: string,
  init: ApiRequestInit,
  parse: (body: unknown) => T,
): Promise<ApiResult<T>> {
  const res = await apiFetch(path, init);
  if (!res) return { ok: false, problem: API_STARTING_PROBLEM };
  if (!res.ok) return { ok: false, problem: await parseProblem(res) };
  try {
    const text = await res.text();
    return {
      ok: true,
      status: res.status,
      data: parse(text ? (JSON.parse(text) as unknown) : null),
    };
  } catch {
    return { ok: false, problem: { status: 502, title: "Unexpected response from the service" } };
  }
}

/** For endpoints whose success body is not needed (register, resend, forgot, reset, confirm). */
export async function apiVoid(path: string, init: ApiRequestInit): Promise<ApiResult<null>> {
  const res = await apiFetch(path, init);
  if (!res) return { ok: false, problem: API_STARTING_PROBLEM };
  if (!res.ok) return { ok: false, problem: await parseProblem(res) };
  await res.body?.cancel();
  return { ok: true, status: res.status, data: null };
}
