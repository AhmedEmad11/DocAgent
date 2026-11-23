import { beforeAll, beforeEach, describe, expect, it, vi } from "vitest";
import { NextRequest } from "next/server";

const authedApiFetch = vi.fn();
vi.mock("@/lib/session/server", () => ({
  authedApiFetch: (...a: unknown[]) => authedApiFetch(...a),
}));

beforeAll(() => {
  process.env.DOCAGENT_API_URL = "https://api.example";
  process.env.SESSION_SECRET = "example-only-session-secret-0123456789abcdef";
  process.env.NEXT_PUBLIC_SITE_URL = "https://ui.example";
  process.env.BFF_SHARED_SECRET = "example-only-bff-secret";
});
beforeEach(() => authedApiFetch.mockReset());

import { STREAM_TIMEOUT_MS, handleAskStream } from "./ask-stream-handler";

const ID = "3f2b8c1e-6a52-4f0e-9d3b-0a1b2c3d4e5f";
const request = (
  body: unknown,
  headers: Record<string, string> = { origin: "https://ui.example" },
) =>
  new NextRequest("https://ui.example/api/ask/stream", {
    method: "POST",
    headers,
    body: typeof body === "string" ? body : JSON.stringify(body),
  });
const sse = (body: BodyInit) => ({
  kind: "response",
  res: new Response(body, { status: 200, headers: { "content-type": "text/event-stream" } }),
});

describe("POST /api/ask/stream", () => {
  it("rejects cross-site requests before anything is forwarded", async () => {
    const res = await handleAskStream(
      request({ question: "q" }, { origin: "https://evil.example" }),
    );
    expect(res.status).toBe(403);
    expect(authedApiFetch).not.toHaveBeenCalled();
  });

  it("validates the body: question length, document ids, JSON", async () => {
    for (const body of [
      '{"question":""}',
      '{"question":"   "}',
      `{"question":"${"x".repeat(2001)}"}`,
      '{"question":"q","documentIds":["not-a-uuid"]}',
      "not json",
      "{}",
      '{"question":"q","documentIds":' + JSON.stringify(Array(101).fill(ID)) + "}",
    ]) {
      expect((await handleAskStream(request(body))).status).toBe(400);
    }
    expect(authedApiFetch).not.toHaveBeenCalled();
  });

  it("refuses oversized bodies", async () => {
    const res = await handleAskStream(request({ question: "q", pad: "x".repeat(70_000) }));
    expect(res.status).toBe(413);
  });

  it("forwards only the validated fields with the abort signal and a long timeout, and relays the stream unbuffered", async () => {
    let controller!: ReadableStreamDefaultController<Uint8Array>;
    const upstream = new ReadableStream<Uint8Array>({ start: (c) => (controller = c) });
    authedApiFetch.mockResolvedValue(sse(upstream));
    const req = request({ question: " Why? ", documentIds: [ID], extra: "dropped" });
    const res = await handleAskStream(req);

    const [path, init] = authedApiFetch.mock.calls[0] as [
      string,
      { json: unknown; signal: AbortSignal; timeoutMs: number; method: string },
    ];
    expect(path).toBe("/ask/stream");
    expect(init.method).toBe("POST");
    expect(init.json).toEqual({ question: "Why?", documentIds: [ID] });
    expect(init.signal).toBe(req.signal);
    expect(init.timeoutMs).toBe(STREAM_TIMEOUT_MS);
    expect(STREAM_TIMEOUT_MS).toBeGreaterThan(150_000); // UI-PERF-4

    expect(res.headers.get("content-type")).toContain("text/event-stream");
    expect(res.headers.get("cache-control")).toContain("no-transform");
    // First event reaches the reader while the upstream is still open (no buffering, UI-PERF-3).
    controller.enqueue(new TextEncoder().encode("event: searching\ndata: {}\n\n"));
    const reader = res.body!.getReader();
    const first = await reader.read();
    expect(new TextDecoder().decode(first.value)).toContain("searching");
    controller.close();
  });

  it("answers 401 session_ended without a session and 503 network when the API is unreachable", async () => {
    authedApiFetch.mockResolvedValueOnce({ kind: "anonymous" });
    const a = await handleAskStream(request({ question: "q" }));
    expect(a.status).toBe(401);
    expect((await a.json()).code).toBe("session_ended");
    authedApiFetch.mockResolvedValueOnce({ kind: "network" });
    expect((await handleAskStream(request({ question: "q" }))).status).toBe(503);
  });

  it("passes the API's refusal (quota, no key, 409, 429) through as Problem Details with Retry-After", async () => {
    authedApiFetch.mockResolvedValue({
      kind: "response",
      res: new Response(
        JSON.stringify({ title: "Quota", status: 429, code: "demo_quota_exceeded" }),
        { status: 429, headers: { "retry-after": "60" } },
      ),
    });
    const res = await handleAskStream(request({ question: "q" }));
    expect(res.status).toBe(429);
    expect(res.headers.get("retry-after")).toBe("60");
    expect((await res.json()).code).toBe("demo_quota_exceeded");
  });

  it("refuses an upstream answer that is not an event stream", async () => {
    authedApiFetch.mockResolvedValue({
      kind: "response",
      res: new Response("<html>", { status: 200, headers: { "content-type": "text/html" } }),
    });
    expect((await handleAskStream(request({ question: "q" }))).status).toBe(502);
  });
});

describe("cold start (UI-GEN-7)", () => {
  it("answers the host's sleep page with api_starting JSON, not HTML", async () => {
    authedApiFetch.mockResolvedValue({
      kind: "response",
      res: new Response("<html>Container is Sleeping</html>", {
        status: 503,
        headers: { "content-type": "text/html" },
      }),
    });
    const res = await handleAskStream(request({ question: "q" }, { origin: "https://ui.example" }));
    expect(res.status).toBe(503);
    expect(res.headers.get("content-type")).toContain("application/json");
    expect(await res.json()).toMatchObject({ code: "api_starting", retryAfter: 5 });
  });
});
