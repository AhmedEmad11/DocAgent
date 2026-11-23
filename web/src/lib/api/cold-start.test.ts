import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from "vitest";

beforeAll(() => {
  process.env.DOCAGENT_API_URL = "https://api.example";
  process.env.SESSION_SECRET = "example-only-session-secret-0123456789abcdef";
  process.env.NEXT_PUBLIC_SITE_URL = "https://ui.example";
  process.env.BFF_SHARED_SECRET = "example-only-bff-secret";
  process.env.DOCAGENT_API_WAKE_URL = "https://wake.example/api/public/wake/abc";
});

import { apiFetch, apiJson, fetchImpl, resetWakeThrottle } from "./client";
import { API_STARTING_PROBLEM, parseProblem } from "./problem";
import { handleStatus } from "./status-handler";

const original = fetchImpl.current;
const sleepPage = () =>
  new Response("<html><title>Container is Sleeping</title></html>", {
    status: 503,
    headers: { "content-type": "text/html; charset=utf-8" },
  });

let calls: { url: string; init: RequestInit }[];
function fake(handler: (url: string) => Response | Error) {
  calls = [];
  fetchImpl.current = async (url, init) => {
    calls.push({ url, init });
    const r = handler(url);
    if (r instanceof Error) throw r;
    return r;
  };
}
const wakeCalls = () => calls.filter((c) => c.url.startsWith("https://wake.example"));

beforeEach(() => {
  vi.useFakeTimers();
  vi.setSystemTime(new Date("2026-01-01T00:00:00Z"));
  resetWakeThrottle();
});
afterEach(() => {
  vi.useRealTimers();
  fetchImpl.current = original;
});

describe("cold start detection", () => {
  it("503 with an HTML body is api_starting; the API's own 503 JSON is not", async () => {
    expect(await parseProblem(sleepPage())).toEqual(API_STARTING_PROBLEM);
    const html = new Response("Waking up your container", {
      status: 503,
      headers: { "content-type": "text/plain" },
    });
    expect(await parseProblem(html)).toMatchObject({ status: 503, title: "Service unavailable" });
    const body = new Response("<p>Container is Sleeping</p>", { status: 503 });
    expect((await parseProblem(body)).code).toBe("api_starting");
    const json = new Response(JSON.stringify({ title: "Down", code: "llm_unavailable" }), {
      status: 503,
      headers: { "content-type": "application/problem+json" },
    });
    expect(await parseProblem(json)).toMatchObject({ code: "llm_unavailable", status: 503 });
  });

  it("an HTML 200 or 502 page is not a cold start", async () => {
    const p = await parseProblem(
      new Response("<html>", { status: 502, headers: { "content-type": "text/html" } }),
    );
    expect(p.code).toBeUndefined();
  });

  it("a network failure or timeout is api_starting through apiJson", async () => {
    fake(() => new Error("fetch failed"));
    const r = await apiJson("/me", {}, (b) => b);
    expect(r).toEqual({ ok: false, problem: API_STARTING_PROBLEM });
  });

  it("the sleep page through apiJson gives api_starting", async () => {
    fake(() => sleepPage());
    const r = await apiJson("/me", {}, (b) => b);
    expect(r).toEqual({ ok: false, problem: API_STARTING_PROBLEM });
  });
});

describe("wake call", () => {
  it("is a bare POST with no body, cookies, tokens or visitor headers", async () => {
    fake((url) => (url.startsWith("https://wake.example") ? new Response("{}") : sleepPage()));
    await apiFetch("/me", {
      accessToken: "AT",
      visitorHeaders: { get: () => "203.0.113.7" },
      headers: { cookie: "x=1" },
    });
    await vi.advanceTimersByTimeAsync(0);
    expect(wakeCalls()).toHaveLength(1);
    const { init } = wakeCalls()[0]!;
    expect(init.method).toBe("POST");
    expect(init.body).toBeUndefined();
    expect(init.headers).toBeUndefined();
    expect(init.signal).toBeDefined();
  });

  it("is deduplicated to once per 20 seconds, then allowed again", async () => {
    fake((url) => (url.startsWith("https://wake.example") ? new Response("{}") : sleepPage()));
    for (let i = 0; i < 5; i++) await apiFetch("/me");
    await vi.advanceTimersByTimeAsync(0);
    expect(wakeCalls()).toHaveLength(1);
    await vi.advanceTimersByTimeAsync(20_001);
    await apiFetch("/me");
    await vi.advanceTimersByTimeAsync(0);
    expect(wakeCalls()).toHaveLength(2);
  });

  it("is not made for healthy or ordinary error answers, and never throws when the wake fails", async () => {
    fake(() => new Response("{}", { status: 200 }));
    await apiFetch("/me");
    fake(() => new Response("{}", { status: 500 }));
    await apiFetch("/me");
    expect(wakeCalls()).toHaveLength(0);
    fake((url) => (url.startsWith("https://wake.example") ? new Error("boom") : sleepPage()));
    await expect(apiFetch("/me")).resolves.toBeInstanceOf(Response);
    await vi.advanceTimersByTimeAsync(0);
  });
});

describe("GET /api/status", () => {
  it("is ready on 200 from /health/live (outside /api/v1) without waking", async () => {
    fake(() => new Response('{"status":"Healthy"}', { status: 200 }));
    const res = await handleStatus();
    expect(await res.json()).toEqual({ ready: true });
    expect(res.status).toBe(200);
    expect(res.headers.get("cache-control")).toBe("no-store");
    expect(calls[0]!.url).toBe("https://api.example/health/live");
    expect(wakeCalls()).toHaveLength(0);
  });

  it("is not ready (still 200) and wakes on the sleep page, on errors and on timeouts", async () => {
    fake((url) => (url.startsWith("https://wake.example") ? new Response("{}") : sleepPage()));
    let res = await handleStatus();
    expect(res.status).toBe(200);
    expect(await res.json()).toEqual({ ready: false });
    await vi.advanceTimersByTimeAsync(0);
    expect(wakeCalls()).toHaveLength(1);

    resetWakeThrottle();
    fake(() => new Error("timeout"));
    res = await handleStatus();
    expect(await res.json()).toEqual({ ready: false });
  });

  it("polling is deduplicated: many not-ready answers make one wake call", async () => {
    fake(() => sleepPage());
    for (let i = 0; i < 6; i++) await handleStatus();
    await vi.advanceTimersByTimeAsync(0);
    expect(wakeCalls()).toHaveLength(1);
  });
});
