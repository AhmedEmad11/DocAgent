import { afterEach, describe, expect, it, vi } from "vitest";
import { bffFetch, problemFromBody } from "./bff";

afterEach(() => vi.unstubAllGlobals());

describe("problemFromBody", () => {
  it("reads title, detail, code and retryAfter and ignores everything else", () => {
    const p = problemFromBody(
      429,
      JSON.stringify({
        title: "Busy",
        detail: "d",
        code: "demo_rate_limited",
        retryAfter: 4.2,
        extra: "x",
      }),
      null,
    );
    expect(p).toEqual({
      status: 429,
      title: "Busy",
      detail: "d",
      code: "demo_rate_limited",
      retryAfter: 5,
    });
  });

  it("falls back to the Retry-After header and survives non-JSON bodies", () => {
    expect(problemFromBody(429, "nope", "7").retryAfter).toBe(7);
    expect(problemFromBody(502, "", null)).toEqual({ status: 502, title: "Error" });
    expect(problemFromBody(500, "[1,2]", null).status).toBe(500);
  });
});

describe("bffFetch", () => {
  it("returns data on success and sends same-origin credentials without a body for GET", async () => {
    const fetchMock = vi.fn(async () => new Response(JSON.stringify([1]), { status: 200 }));
    vi.stubGlobal("fetch", fetchMock);
    const res = await bffFetch<number[]>("/api/proxy/documents");
    expect(res).toEqual({ ok: true, data: [1] });
    const init = (fetchMock.mock.calls[0] as unknown as [string, RequestInit])[1];
    expect(init.credentials).toBe("same-origin");
    expect(init.body).toBeUndefined();
  });

  it("turns an error response into a problem and a thrown fetch into the network problem", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn(
        async () => new Response(JSON.stringify({ title: "Gone", status: 404 }), { status: 404 }),
      ),
    );
    expect(await bffFetch("/x")).toEqual({ ok: false, problem: { status: 404, title: "Gone" } });
    vi.stubGlobal(
      "fetch",
      vi.fn(async () => {
        throw new TypeError("offline");
      }),
    );
    const res = await bffFetch("/x");
    expect(res.ok).toBe(false);
    if (!res.ok) expect(res.problem.code).toBe("network");
  });

  it("handles 204 with no body", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn(async () => new Response(null, { status: 204 })),
    );
    expect(await bffFetch("/x", { method: "DELETE" })).toEqual({ ok: true, data: null });
  });
});

describe("bffFetch cold start retry (UI-GEN-7)", () => {
  it("waits for /api/status, retries the same call once and returns the second answer", async () => {
    const startingBody = JSON.stringify({ status: 503, title: "s", code: "api_starting" });
    const fetchMock = vi.fn(async (url: string) => {
      if (url === "/api/status") return new Response(JSON.stringify({ ready: true }));
      return fetchMock.mock.calls.filter((c) => c[0] === "/api/proxy/x").length === 1
        ? new Response(startingBody, { status: 503 })
        : new Response(JSON.stringify({ v: 1 }));
    });
    vi.stubGlobal("fetch", fetchMock);
    const r = await bffFetch<{ v: number }>("/api/proxy/x");
    expect(r).toEqual({ ok: true, data: { v: 1 } });
    expect(fetchMock.mock.calls.filter((c) => c[0] === "/api/proxy/x")).toHaveLength(2);
  });

  it("never retries more than once: a second api_starting is the result", async () => {
    const fetchMock = vi.fn(async (url: string) =>
      url === "/api/status"
        ? new Response(JSON.stringify({ ready: true }))
        : new Response(JSON.stringify({ status: 503, title: "s", code: "api_starting" }), {
            status: 503,
          }),
    );
    vi.stubGlobal("fetch", fetchMock);
    const r = await bffFetch("/api/proxy/x");
    expect(r).toMatchObject({ ok: false, problem: { code: "api_starting" } });
    expect(fetchMock.mock.calls.filter((c) => c[0] === "/api/proxy/x")).toHaveLength(2);
  });
});
