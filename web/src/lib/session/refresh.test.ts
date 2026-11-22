import { beforeEach, describe, expect, it, vi } from "vitest";

const refreshApi = vi.fn();
vi.mock("@/lib/api/auth", () => ({ refresh: (...args: unknown[]) => refreshApi(...args) }));

import { RECENT_TTL_MS, refreshSession, toSessionData } from "./refresh";

const session = { at: "old-at", rt: "old-rt", ate: 0 };
const tokens = {
  tokenType: "Bearer",
  accessToken: "new-at",
  expiresIn: 3600,
  refreshToken: "new-rt",
};

beforeEach(() => {
  refreshApi.mockReset();
});

describe("refreshSession", () => {
  it("turns the API's token response into session data with the access expiry", () => {
    expect(toSessionData(tokens, 1_000_000)).toEqual({
      at: "new-at",
      rt: "new-rt",
      ate: 1000 + 3600,
    });
  });

  it("replaces both tokens on success", async () => {
    refreshApi.mockResolvedValue({ ok: true, status: 200, data: tokens });
    const out = await refreshSession(session, {});
    expect(out).toMatchObject({ kind: "ok", session: { at: "new-at", rt: "new-rt" } });
    expect(refreshApi).toHaveBeenCalledWith({}, "old-rt");
  });

  it("shares one API call between concurrent refreshes of the same session (race)", async () => {
    const raced = { ...session, rt: "race-rt" };
    let release!: () => void;
    refreshApi.mockImplementation(
      () =>
        new Promise(
          (resolve) => (release = () => resolve({ ok: true, status: 200, data: tokens })),
        ),
    );
    const all = Promise.all([
      refreshSession(raced, {}),
      refreshSession(raced, {}),
      refreshSession(raced, {}),
    ]);
    release();
    const results = await all;
    expect(refreshApi).toHaveBeenCalledTimes(1);
    expect(results.every((r) => r.kind === "ok")).toBe(true);
  });

  it("separates 'the API refused the token' (sign in again) from 'the API is down' (keep the session)", async () => {
    refreshApi.mockResolvedValue({ ok: false, problem: { status: 401, title: "Unauthorized" } });
    expect(await refreshSession({ ...session, rt: "a" }, {})).toEqual({ kind: "invalid" });
    refreshApi.mockResolvedValue({ ok: false, problem: { status: 503, title: "x" } });
    expect(await refreshSession({ ...session, rt: "b" }, {})).toEqual({ kind: "unavailable" });
    refreshApi.mockResolvedValue({
      ok: false,
      problem: { status: 0, title: "x", code: "network" },
    });
    expect(await refreshSession({ ...session, rt: "c" }, {})).toEqual({ kind: "unavailable" });
  });

  it("late requests still holding the old cookie reuse a refresh that just finished, for a few seconds only", async () => {
    vi.useFakeTimers();
    try {
      refreshApi.mockResolvedValue({ ok: true, status: 200, data: tokens });
      const stale = { at: "a", rt: "stale-rt", ate: 0 };
      const first = await refreshSession(stale, {});
      const second = await refreshSession(stale, {});
      expect(second).toEqual(first);
      expect(refreshApi).toHaveBeenCalledTimes(1);

      vi.advanceTimersByTime(RECENT_TTL_MS + 1);
      await refreshSession(stale, {});
      expect(refreshApi).toHaveBeenCalledTimes(2);
    } finally {
      vi.useRealTimers();
    }
  });

  it("does not remember failures", async () => {
    refreshApi.mockResolvedValue({ ok: false, problem: { status: 503, title: "x" } });
    await refreshSession({ at: "a", rt: "flaky-rt", ate: 0 }, {});
    refreshApi.mockResolvedValue({ ok: true, status: 200, data: tokens });
    expect(await refreshSession({ at: "a", rt: "flaky-rt", ate: 0 }, {})).toMatchObject({
      kind: "ok",
    });
  });
});
