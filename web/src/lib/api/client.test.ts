import { afterEach, beforeAll, describe, expect, it, vi } from "vitest";

beforeAll(() => {
  process.env.DOCAGENT_API_URL = "https://api.example";
  process.env.SESSION_SECRET = "example-only-session-secret-0123456789abcdef";
  process.env.NEXT_PUBLIC_SITE_URL = "https://ui.example";
  process.env.BFF_SHARED_SECRET = "example-only-bff-secret";
});

import { apiFetch, apiJson, apiVoid, fetchImpl } from "./client";
import { confirmEmail, getMe, login } from "./auth";

const original = fetchImpl.current;
afterEach(() => {
  fetchImpl.current = original;
});

function fake(response: Response | (() => Response) | Error) {
  const calls: { url: string; init: RequestInit }[] = [];
  fetchImpl.current = async (url, init) => {
    calls.push({ url, init });
    if (response instanceof Error) throw response;
    return typeof response === "function" ? response() : response;
  };
  return calls;
}

const h = (o: Record<string, string>) => ({ get: (n: string) => o[n.toLowerCase()] ?? null });

describe("API client (server to server)", () => {
  it("builds the URL from DOCAGENT_API_URL, sends JSON and the bearer token, never follows redirects", async () => {
    const calls = fake(new Response("{}", { status: 200 }));
    await apiFetch("/me", { accessToken: "AT", json: { a: 1 }, method: "POST" });
    const { url, init } = calls[0]!;
    expect(url).toBe("https://api.example/api/v1/me");
    const headers = init.headers as Record<string, string>;
    expect(headers.authorization).toBe("Bearer AT");
    expect(headers["content-type"]).toBe("application/json");
    expect(init.body).toBe('{"a":1}');
    expect(init.redirect).toBe("manual");
    expect(init.cache).toBe("no-store");
  });

  it("forwards the visitor's IP only together with the shared secret (D41, A4)", async () => {
    const calls = fake(new Response("{}"));
    await apiFetch("/auth/login", { visitorHeaders: h({ "x-forwarded-for": "203.0.113.7" }) });
    const headers = calls[0]!.init.headers as Record<string, string>;
    expect(headers["x-client-ip"]).toBe("203.0.113.7");
    expect(headers["x-bff-secret"]).toBe("example-only-bff-secret");

    const calls2 = fake(new Response("{}"));
    await apiFetch("/auth/login", { visitorHeaders: h({}) });
    const headers2 = calls2[0]!.init.headers as Record<string, string>;
    expect(headers2["x-client-ip"]).toBeUndefined();
    expect(headers2["x-bff-secret"]).toBeUndefined(); // no IP, no need to show the secret
  });

  it("never forwards the browser's cookies or other headers", async () => {
    const calls = fake(new Response("{}"));
    await apiFetch("/me", {
      visitorHeaders: h({
        cookie: "docagent_session=SEALED",
        authorization: "Bearer X",
        "x-forwarded-for": "203.0.113.7",
      }),
    });
    const headers = calls[0]!.init.headers as Record<string, string>;
    expect(Object.keys(headers).sort()).toEqual(["accept", "x-bff-secret", "x-client-ip"]);
  });

  it("returns null when the API cannot be reached", async () => {
    fake(new TypeError("fetch failed"));
    expect(await apiFetch("/me")).toBeNull();
  });

  it("apiJson: ok, problem, network and malformed bodies", async () => {
    fake(new Response('{"v":1}'));
    expect(await apiJson("/x", {}, (b) => b as { v: number })).toEqual({
      ok: true,
      status: 200,
      data: { v: 1 },
    });

    fake(new Response(JSON.stringify({ title: "Nope", code: "c" }), { status: 409 }));
    expect(await apiJson("/x", {}, (b) => b)).toEqual({
      ok: false,
      problem: { status: 409, title: "Nope", code: "c" },
    });

    fake(new Error("down"));
    expect(await apiJson("/x", {}, (b) => b)).toMatchObject({
      ok: false,
      problem: { status: 0, code: "network" },
    });

    fake(new Response("not json"));
    expect(await apiJson("/x", {}, (b) => b)).toMatchObject({
      ok: false,
      problem: { status: 502 },
    });
  });

  it("apiVoid ignores the success body", async () => {
    fake(new Response("Thank you for confirming your email."));
    expect(await apiVoid("/x", {})).toEqual({ ok: true, status: 200, data: null });
    fake(new Response(null, { status: 401 }));
    expect(await apiVoid("/x", {})).toMatchObject({ ok: false, problem: { status: 401 } });
  });
});

describe("typed auth calls", () => {
  it("login parses the token response and rejects an unexpected shape", async () => {
    fake(
      new Response(
        JSON.stringify({
          tokenType: "Bearer",
          accessToken: "a",
          expiresIn: 3600,
          refreshToken: "r",
        }),
      ),
    );
    expect(await login({}, "a@example.com", "Example-Only-1")).toEqual({
      ok: true,
      status: 200,
      data: { tokenType: "Bearer", accessToken: "a", expiresIn: 3600, refreshToken: "r" },
    });
    fake(new Response(JSON.stringify({ accessToken: "a" })));
    expect(await login({}, "a@example.com", "Example-Only-1")).toMatchObject({ ok: false });
  });

  it("login sends credentials in the JSON body, not the URL", async () => {
    const calls = fake(new Response("{}", { status: 401 }));
    await login({}, "a@example.com", "Example-Only-1");
    expect(calls[0]!.url).toBe("https://api.example/api/v1/auth/login");
    expect(calls[0]!.url).not.toContain("Example-Only-1");
    expect(JSON.parse(calls[0]!.init.body as string)).toEqual({
      email: "a@example.com",
      password: "Example-Only-1",
    });
  });

  it("confirmEmail escapes the query values", async () => {
    const calls = fake(new Response("ok"));
    await confirmEmail({}, "u&1", "a+b/c=");
    expect(calls[0]!.url).toBe(
      "https://api.example/api/v1/auth/confirmEmail?userId=u%261&code=a%2Bb%2Fc%3D",
    );
  });

  it("getMe validates the account shape", async () => {
    fake(
      new Response(
        JSON.stringify({
          id: "1",
          email: "a@example.com",
          emailConfirmed: true,
          externalLogins: ["Google"],
          hasLlmKey: false,
          chunks: { used: 0, limit: 10 },
        }),
      ),
    );
    const r = await getMe({}, "AT");
    expect(r).toMatchObject({
      ok: true,
      data: { email: "a@example.com", externalLogins: ["Google"] },
    });
  });
});

describe("timeouts", () => {
  it("aborts a call that takes too long", async () => {
    vi.useFakeTimers();
    fetchImpl.current = (_url, init) =>
      new Promise((_resolve, reject) =>
        init.signal?.addEventListener("abort", () => reject(new Error("aborted"))),
      );
    const p = apiFetch("/me", { timeoutMs: 50 });
    await vi.advanceTimersByTimeAsync(60);
    expect(await p).toBeNull();
    vi.useRealTimers();
  });
});
