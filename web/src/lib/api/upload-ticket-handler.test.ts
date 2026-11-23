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

import { handleUploadTicket } from "./upload-ticket-handler";

const request = (headers: Record<string, string> = { origin: "https://ui.example" }) =>
  new NextRequest("https://ui.example/api/upload/ticket", { method: "POST", headers });

describe("POST /api/upload/ticket", () => {
  it("rejects cross-site requests before any API call (UI-SEC-3)", async () => {
    const res = await handleUploadTicket(request({ origin: "https://evil.example" }));
    expect(res.status).toBe(403);
    expect(authedApiFetch).not.toHaveBeenCalled();
  });

  it("returns the ticket and the API upload URL, never a token", async () => {
    authedApiFetch.mockResolvedValue({
      kind: "response",
      res: new Response(JSON.stringify({ ticket: "x".repeat(43), expiresInSeconds: 60 }), {
        status: 200,
      }),
    });
    const res = await handleUploadTicket(request());
    expect(res.status).toBe(200);
    const body = await res.json();
    expect(body).toEqual({
      ticket: "x".repeat(43),
      expiresInSeconds: 60,
      uploadUrl: "https://api.example/api/v1/documents",
    });
    expect(res.headers.get("cache-control")).toBe("no-store");
    expect(authedApiFetch).toHaveBeenCalledWith("/documents/upload-ticket", { method: "POST" });
  });

  it("answers 401 session_ended without a session and 503 api_starting when the API is unreachable", async () => {
    authedApiFetch.mockResolvedValueOnce({ kind: "anonymous" });
    const a = await handleUploadTicket(request());
    expect(a.status).toBe(401);
    expect((await a.json()).code).toBe("session_ended");
    authedApiFetch.mockResolvedValueOnce({ kind: "network" });
    const n = await handleUploadTicket(request());
    expect(n.status).toBe(503);
    expect((await n.json()).code).toBe("api_starting");
  });

  it("passes API problems through in the fixed shape, with Retry-After", async () => {
    authedApiFetch.mockResolvedValue({
      kind: "response",
      res: new Response(JSON.stringify({ title: "Too many requests", status: 429 }), {
        status: 429,
        headers: { "retry-after": "5" },
      }),
    });
    const res = await handleUploadTicket(request());
    expect(res.status).toBe(429);
    expect(res.headers.get("retry-after")).toBe("5");
  });

  it("refuses a malformed API answer", async () => {
    authedApiFetch.mockResolvedValue({
      kind: "response",
      res: new Response("{}", { status: 200 }),
    });
    expect((await handleUploadTicket(request())).status).toBe(502);
  });
});
