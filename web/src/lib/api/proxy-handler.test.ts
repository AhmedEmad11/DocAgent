import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from "vitest";
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
afterEach(() => vi.restoreAllMocks());

import { handleProxy } from "./proxy-handler";

const KEY = "sk-example-only-not-a-real-key-123456";
const same = { origin: "https://ui.example" };

describe("generic proxy: LLM key handling (UI-KEY-3, UI-KEY-5, UI-SEC-6)", () => {
  it("forwards the key header for the model list only, as a header and never in the URL, and does not log it", async () => {
    const spies = (["log", "info", "warn", "error", "debug"] as const).map((m) =>
      vi.spyOn(console, m).mockImplementation(() => {}),
    );
    authedApiFetch.mockResolvedValue({
      kind: "response",
      res: new Response(JSON.stringify({ provider: "openai", models: [] }), {
        status: 200,
        headers: { "content-type": "application/json" },
      }),
    });
    const req = new NextRequest("https://ui.example/api/proxy/providers/openai/models", {
      headers: {
        ...same,
        "x-llm-api-key": KEY,
        cookie: "docagent_session=sealed",
        "x-forwarded-for": "203.0.113.9",
      },
    });
    const res = await handleProxy(req, ["providers", "openai", "models"]);

    expect(res.status).toBe(200);
    const [path, init] = authedApiFetch.mock.calls[0] as [
      string,
      { headers: Record<string, string> },
    ];
    expect(path).toBe("/providers/openai/models");
    expect(path).not.toContain(KEY);
    expect(init.headers).toEqual({ "x-llm-api-key": KEY }); // nothing else is forwarded: no cookie, no forwarded-for
    expect(JSON.stringify(Object.fromEntries(res.headers))).not.toContain(KEY);
    expect(await res.text()).not.toContain(KEY);
    for (const spy of spies) expect(JSON.stringify(spy.mock.calls)).not.toContain("sk-example");
  });

  it("does not forward the key header on any other route", async () => {
    authedApiFetch.mockResolvedValue({
      kind: "response",
      res: new Response("{}", { status: 200 }),
    });
    await handleProxy(
      new NextRequest("https://ui.example/api/proxy/me", {
        headers: { ...same, "x-llm-api-key": KEY },
      }),
      ["me"],
    );
    expect(
      (authedApiFetch.mock.calls[0] as [string, { headers: Record<string, string> }])[1].headers,
    ).toEqual({});
  });

  it("saves the key with a JSON body, passes the API's refusal back without the key, and does not log", async () => {
    const spies = (["log", "info", "warn", "error"] as const).map((m) =>
      vi.spyOn(console, m).mockImplementation(() => {}),
    );
    authedApiFetch.mockResolvedValue({
      kind: "response",
      res: new Response(
        JSON.stringify({ title: "LLM key rejected", status: 400, code: "llm_key_invalid" }),
        { status: 400 },
      ),
    });
    const req = new NextRequest("https://ui.example/api/proxy/me/llm-key", {
      method: "PUT",
      headers: { ...same, "content-type": "application/json" },
      body: JSON.stringify({ provider: "openai", model: "gpt-4.1", apiKey: KEY }),
    });
    const res = await handleProxy(req, ["me", "llm-key"]);
    expect(res.status).toBe(400);
    expect(await res.text()).not.toContain(KEY);
    expect((authedApiFetch.mock.calls[0] as [string, { body: string }])[1].body).toContain(KEY);
    for (const spy of spies) expect(JSON.stringify(spy.mock.calls)).not.toContain("sk-example");
  });

  it("refuses a cross-site key save before anything is forwarded", async () => {
    const req = new NextRequest("https://ui.example/api/proxy/me/llm-key", {
      method: "PUT",
      headers: { origin: "https://evil.example", "content-type": "application/json" },
      body: JSON.stringify({ provider: "openai", model: "m", apiKey: KEY }),
    });
    expect((await handleProxy(req, ["me", "llm-key"])).status).toBe(403);
    expect(authedApiFetch).not.toHaveBeenCalled();
  });

  it("gives long calls their timeout and others the default", async () => {
    authedApiFetch.mockResolvedValue({
      kind: "response",
      res: new Response("{}", { status: 200 }),
    });
    await handleProxy(
      new NextRequest("https://ui.example/api/proxy/documents/compare", {
        method: "POST",
        headers: { ...same, "content-type": "application/json" },
        body: "{}",
      }),
      ["documents", "compare"],
    );
    await handleProxy(new NextRequest("https://ui.example/api/proxy/me", { headers: same }), [
      "me",
    ]);
    expect((authedApiFetch.mock.calls[0] as [string, { timeoutMs?: number }])[1].timeoutMs).toBe(
      170_000,
    );
    expect(
      (authedApiFetch.mock.calls[1] as [string, { timeoutMs?: number }])[1].timeoutMs,
    ).toBeUndefined();
  });
});
