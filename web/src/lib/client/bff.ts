import { NETWORK_PROBLEM, type ApiProblem } from "@/lib/api/problem";

/** Reads a Problem Details body (ours or the API's) without ever throwing. */
export function problemFromBody(
  status: number,
  text: string,
  retryAfterHeader: string | null,
): ApiProblem {
  let body: Record<string, unknown> = {};
  try {
    const parsed: unknown = text ? JSON.parse(text) : {};
    if (parsed !== null && typeof parsed === "object") body = parsed as Record<string, unknown>;
  } catch {
    // not JSON
  }
  const problem: ApiProblem = {
    status,
    title: typeof body.title === "string" ? body.title : "Error",
  };
  if (typeof body.detail === "string") problem.detail = body.detail;
  if (typeof body.code === "string") problem.code = body.code;
  const after = Number(body.retryAfter ?? retryAfterHeader);
  if (Number.isFinite(after) && after > 0) problem.retryAfter = Math.ceil(after);
  return problem;
}

export type BffResult<T> = { ok: true; data: T } | { ok: false; problem: ApiProblem };

/**
 * Browser call to one of our own BFF routes (never to the API). Same origin, so the session cookie rides along and the
 * browser adds the Origin header the CSRF check wants. Returns a problem in the fixed shape (SRS 10.6) instead of throwing.
 */
export async function bffFetch<T>(
  path: string,
  init: {
    method?: "GET" | "POST" | "PUT" | "DELETE";
    json?: unknown;
    headers?: Record<string, string>;
    signal?: AbortSignal;
  } = {},
  parse: (body: unknown) => T = (b) => b as T,
): Promise<BffResult<T>> {
  try {
    const res = await fetch(path, {
      method: init.method ?? "GET",
      credentials: "same-origin",
      cache: "no-store",
      headers: {
        accept: "application/json",
        ...(init.json !== undefined ? { "content-type": "application/json" } : {}),
        ...init.headers,
      },
      ...(init.json !== undefined ? { body: JSON.stringify(init.json) } : {}),
      ...(init.signal ? { signal: init.signal } : {}),
    });
    const text = await res.text();
    if (!res.ok)
      return {
        ok: false,
        problem: problemFromBody(res.status, text, res.headers.get("retry-after")),
      };
    return { ok: true, data: parse(text ? (JSON.parse(text) as unknown) : null) };
  } catch (e) {
    if (e instanceof DOMException && e.name === "AbortError") throw e;
    return { ok: false, problem: NETWORK_PROBLEM };
  }
}
