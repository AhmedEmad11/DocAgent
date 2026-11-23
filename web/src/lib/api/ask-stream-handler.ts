import { NextResponse, type NextRequest } from "next/server";
import { z } from "zod";
import { getServerEnv } from "@/lib/env";
import { isSameOrigin } from "@/lib/auth/origin";
import { authedApiFetch } from "@/lib/session/server";
import { parseProblem, type ApiProblem } from "./problem";

const NO_STORE = { "cache-control": "no-store" } as const;
/** Longer than the BYOK agent timeout (D20: 120 s) plus margin (UI-PERF-4); the route's `maxDuration` is above it. */
export const STREAM_TIMEOUT_MS = 170_000;
const MAX_BODY_BYTES = 64 * 1024;

const bodySchema = z.object({
  question: z.string().trim().min(1).max(2000),
  documentIds: z.array(z.uuid()).max(100).optional(),
});

function problem(p: ApiProblem): NextResponse {
  const headers: Record<string, string> = { ...NO_STORE };
  if (p.retryAfter) headers["retry-after"] = String(p.retryAfter);
  return NextResponse.json(p, { status: p.status || 502, headers });
}

/**
 * `POST /api/ask/stream` (UI-QA-2, UI-PERF-3): relays the API's Server-Sent Events to the browser without buffering. The API's response
 * body is handed on as a stream, so the first event reaches the browser when the API sends it. The browser's abort (`req.signal`) is passed
 * to the upstream request, so cancelling stops the work on the API (UI-QA-7). The access token is added here and never leaves the server.
 * Errors before the stream starts are normal Problem Details responses; errors during the stream arrive as `error` events.
 */
export async function handleAskStream(req: NextRequest): Promise<Response> {
  if (!isSameOrigin(req.headers, getServerEnv().NEXT_PUBLIC_SITE_URL))
    return problem({ status: 403, title: "Forbidden", detail: "Cross-site request rejected." });

  const declared = Number(req.headers.get("content-length") ?? "0");
  if (Number.isFinite(declared) && declared > MAX_BODY_BYTES)
    return problem({ status: 413, title: "Payload too large" });
  let text: string;
  try {
    text = await req.text();
  } catch {
    return problem({ status: 400, title: "Bad request" });
  }
  if (text.length > MAX_BODY_BYTES) return problem({ status: 413, title: "Payload too large" });

  let parsed;
  try {
    parsed = bodySchema.safeParse(JSON.parse(text));
  } catch {
    return problem({ status: 400, title: "Bad request" });
  }
  if (!parsed.success) return problem({ status: 400, title: "Bad request" });

  const result = await authedApiFetch("/ask/stream", {
    method: "POST",
    json: parsed.data,
    headers: { accept: "text/event-stream" },
    signal: req.signal,
    timeoutMs: STREAM_TIMEOUT_MS,
  });
  if (result.kind === "anonymous")
    return problem({ status: 401, title: "Unauthorized", code: "session_ended" });
  if (result.kind === "network")
    return problem({ status: 503, title: "Cannot reach the service", code: "network" });

  const { res } = result;
  if (!res.ok) return problem(await parseProblem(res));
  if (!res.body || !(res.headers.get("content-type") ?? "").startsWith("text/event-stream")) {
    await res.body?.cancel();
    return problem({ status: 502, title: "Unexpected response from the service" });
  }

  return new Response(res.body, {
    status: 200,
    headers: {
      "content-type": "text/event-stream; charset=utf-8",
      // no-transform keeps compression middleware from buffering the stream; X-Accel-Buffering for proxies that honour it.
      "cache-control": "no-store, no-transform",
      "x-accel-buffering": "no",
    },
  });
}
