import { NextResponse, type NextRequest } from "next/server";
import { getServerEnv } from "@/lib/env";
import { isSameOrigin } from "@/lib/auth/origin";
import { authedApiFetch } from "@/lib/session/server";
import { matchAllowed } from "./allowlist";
import { parseProblem, type ApiProblem } from "./problem";

const NO_STORE = { "cache-control": "no-store" } as const;

function problemResponse(problem: ApiProblem): NextResponse {
  const headers: Record<string, string> = { ...NO_STORE };
  if (problem.retryAfter) headers["retry-after"] = String(problem.retryAfter);
  return NextResponse.json(problem, { status: problem.status || 502, headers });
}

async function readLimited(req: NextRequest, max: number): Promise<string | null> {
  if (max === 0) return "";
  const declared = Number(req.headers.get("content-length") ?? "0");
  if (Number.isFinite(declared) && declared > max) return null;
  const text = await req.text();
  return new TextEncoder().encode(text).length > max ? null : text;
}

/** Generic allowlisted proxy (UI-SEC-7): browser -> BFF -> API with the session's token; the token never leaves the server. */
export async function handleProxy(req: NextRequest, segments: string[]): Promise<NextResponse> {
  const match = matchAllowed(req.method, segments);
  if (!match) return problemResponse({ status: 404, title: "Not found" });

  if (req.method !== "GET" && !isSameOrigin(req.headers, getServerEnv().NEXT_PUBLIC_SITE_URL)) {
    return problemResponse({
      status: 403,
      title: "Forbidden",
      detail: "Cross-site request rejected.",
    });
  }

  const body = await readLimited(req, match.route.maxBodyBytes);
  if (body === null) return problemResponse({ status: 413, title: "Payload too large" });

  const headers: Record<string, string> = {};
  for (const name of match.route.headers ?? []) {
    const value = req.headers.get(name);
    if (value) headers[name] = value;
  }

  const result = await authedApiFetch(match.path, {
    method: req.method as "GET" | "POST" | "PUT" | "DELETE",
    headers,
    body: body || undefined,
    signal: req.signal,
    ...(match.route.timeoutMs ? { timeoutMs: match.route.timeoutMs } : {}),
  });
  if (result.kind === "anonymous")
    return problemResponse({ status: 401, title: "Unauthorized", code: "session_ended" });
  if (result.kind === "network")
    return problemResponse({ status: 503, title: "Cannot reach the service", code: "network" });

  const { res } = result;
  if (!res.ok) return problemResponse(await parseProblem(res));
  const out = new Headers(NO_STORE);
  const type = res.headers.get("content-type");
  if (type) out.set("content-type", type);
  return new NextResponse(res.status === 204 ? null : res.body, {
    status: res.status,
    headers: out,
  });
}
