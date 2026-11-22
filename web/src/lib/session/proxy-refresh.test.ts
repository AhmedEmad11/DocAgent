import { beforeAll, beforeEach, describe, expect, it, vi } from "vitest";

const refreshApi = vi.fn();
vi.mock("@/lib/api/auth", () => ({ refresh: (...args: unknown[]) => refreshApi(...args) }));

beforeAll(() => {
  process.env.DOCAGENT_API_URL = "https://api.example";
  process.env.SESSION_SECRET = "example-only-session-secret-0123456789abcdef";
  process.env.NEXT_PUBLIC_SITE_URL = "https://ui.example";
  process.env.BFF_SHARED_SECRET = "example-only-bff-secret";
});

import { refreshForRequest } from "./proxy-refresh";
import { sealSessionData, unsealSessionData } from "./model";

const secrets = { current: "example-only-session-secret-0123456789abcdef" };
const nowSec = () => Math.floor(Date.now() / 1000);
const COOKIE = "__Host-docagent_session";
const req = (value?: string) => ({
  headers: { get: () => null },
  cookies: { get: (n: string) => (n === COOKIE && value ? { value } : undefined) },
});
const tokens = {
  tokenType: "Bearer",
  accessToken: "new-at",
  expiresIn: 3600,
  refreshToken: "new-rt",
};

beforeEach(() => {
  refreshApi.mockReset();
});

describe("refreshForRequest (UI-AUTH-8, runs in the proxy before pages render)", () => {
  it("does nothing without a cookie or while the access token is fresh", async () => {
    expect(await refreshForRequest(req())).toEqual({ action: "none" });
    const fresh = await sealSessionData({ at: "a", rt: "r", ate: nowSec() + 3600 }, secrets);
    expect(await refreshForRequest(req(fresh))).toEqual({ action: "none" });
    expect(refreshApi).not.toHaveBeenCalled();
  });

  it("refreshes a token that is about to expire and returns a new sealed cookie with both new tokens", async () => {
    refreshApi.mockResolvedValue({ ok: true, status: 200, data: tokens });
    const old = await sealSessionData({ at: "old-a", rt: "old-r", ate: nowSec() + 10 }, secrets);
    const out = await refreshForRequest(req(old));
    expect(out.action).toBe("set");
    if (out.action !== "set") return;
    expect(out.name).toBe(COOKIE);
    expect(await unsealSessionData(out.value, secrets)).toMatchObject({
      at: "new-at",
      rt: "new-rt",
    });
    expect(refreshApi).toHaveBeenCalledTimes(1);
  });

  it("clears the cookie when the API refuses the refresh token", async () => {
    refreshApi.mockResolvedValue({ ok: false, problem: { status: 401, title: "Unauthorized" } });
    const old = await sealSessionData({ at: "a", rt: "r-refused", ate: nowSec() - 5 }, secrets);
    expect(await refreshForRequest(req(old))).toEqual({ action: "clear", name: COOKIE });
  });

  it("keeps the session when the API is unreachable", async () => {
    refreshApi.mockResolvedValue({
      ok: false,
      problem: { status: 0, title: "x", code: "network" },
    });
    const old = await sealSessionData({ at: "a", rt: "r-down", ate: nowSec() - 5 }, secrets);
    expect(await refreshForRequest(req(old))).toEqual({ action: "none" });
  });

  it("removes a cookie that cannot be opened (tampered, expired, retired secret)", async () => {
    expect(await refreshForRequest(req("garbage.value"))).toEqual({
      action: "clear",
      name: COOKIE,
    });
    expect(refreshApi).not.toHaveBeenCalled();
  });
});
