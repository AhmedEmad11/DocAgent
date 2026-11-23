import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  actionWithColdStartRetry,
  GIVE_UP_AFTER_MS,
  ReadinessController,
  withColdStartRetry,
} from "./controller";

beforeEach(() => vi.useFakeTimers());
afterEach(() => vi.useRealTimers());

function make(answers: boolean[]) {
  const check = vi.fn(async () => (answers.length > 1 ? answers.shift()! : answers[0]!));
  return { check, c: new ReadinessController(check) };
}

describe("ReadinessController", () => {
  it("prewarm: ready means no notice and one check", async () => {
    const { c, check } = make([true]);
    c.prewarm();
    c.prewarm();
    await vi.advanceTimersByTimeAsync(10_000);
    expect(check).toHaveBeenCalledTimes(1);
    expect(c.getSnapshot().phase).toBe("idle");
  });

  it("prewarm: not ready starts the wait, counts seconds, polls every 3 s and ends when ready", async () => {
    const { c, check } = make([false, false, false, true]);
    c.prewarm();
    await vi.advanceTimersByTimeAsync(0);
    expect(c.getSnapshot()).toEqual({ phase: "starting", elapsed: 0 });
    await vi.advanceTimersByTimeAsync(4000);
    expect(c.getSnapshot().elapsed).toBe(4);
    await vi.advanceTimersByTimeAsync(3000);
    expect(c.getSnapshot().phase).toBe("idle");
    expect(check.mock.calls.length).toBeGreaterThanOrEqual(4);
  });

  it("waitUntilReady resolves true when ready; concurrent callers share the wait", async () => {
    const { c, check } = make([false, false, true]);
    const a = c.waitUntilReady();
    const b = c.waitUntilReady();
    await vi.advanceTimersByTimeAsync(7000);
    expect(await Promise.all([a, b])).toEqual([true, true]);
    expect(check.mock.calls.length).toBeLessThanOrEqual(4);
  });

  it("gives up after about three minutes, resolves false, and retry starts over", async () => {
    const { c } = make([false]);
    const p = c.waitUntilReady();
    await vi.advanceTimersByTimeAsync(GIVE_UP_AFTER_MS + 1000);
    expect(await p).toBe(false);
    expect(c.getSnapshot().phase).toBe("timeout");
    c.retry();
    expect(c.getSnapshot()).toEqual({ phase: "starting", elapsed: 0 });
    await vi.advanceTimersByTimeAsync(GIVE_UP_AFTER_MS + 1000);
    expect(c.getSnapshot().phase).toBe("timeout");
  });

  it("a failing check counts as not ready and notifies subscribers", async () => {
    const check = vi.fn(async () => {
      throw new Error("offline");
    });
    const c = new ReadinessController(check);
    const seen: string[] = [];
    c.subscribe(() => seen.push(c.getSnapshot().phase));
    void c.waitUntilReady();
    await vi.advanceTimersByTimeAsync(3500);
    expect(c.getSnapshot().phase).toBe("starting");
    expect(seen).toContain("starting");
  });
});

describe("withColdStartRetry", () => {
  const ctl = (ready: boolean) => ({ waitUntilReady: vi.fn(async () => ready) });

  it("runs once when the first answer is fine", async () => {
    const run = vi.fn(async () => "ok");
    expect(await withColdStartRetry(ctl(true), run, () => false)).toBe("ok");
    expect(run).toHaveBeenCalledTimes(1);
  });

  it("waits and retries exactly once; a second failure is returned as it is", async () => {
    const run = vi.fn(async () => "starting");
    const c = ctl(true);
    expect(await withColdStartRetry(c, run, (r) => r === "starting")).toBe("starting");
    expect(run).toHaveBeenCalledTimes(2);
    expect(c.waitUntilReady).toHaveBeenCalledTimes(1);
  });

  it("does not retry when the wait timed out", async () => {
    const run = vi.fn(async () => "starting");
    await withColdStartRetry(ctl(false), run, () => true);
    expect(run).toHaveBeenCalledTimes(1);
  });

  it("form actions: resubmits the same call once on formError apiStarting", async () => {
    // uses the shared controller: make its check answer ready at once
    vi.stubGlobal(
      "fetch",
      vi.fn(async () => new Response(JSON.stringify({ ready: true }))),
    );
    const run = vi
      .fn<() => Promise<{ ok: boolean; formError?: string }>>()
      .mockResolvedValueOnce({ ok: false, formError: "apiStarting" })
      .mockResolvedValueOnce({ ok: true });
    expect(await actionWithColdStartRetry(run)).toEqual({ ok: true });
    expect(run).toHaveBeenCalledTimes(2);
    vi.unstubAllGlobals();
  });
});
