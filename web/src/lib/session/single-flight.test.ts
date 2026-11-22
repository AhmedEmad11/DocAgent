import { describe, expect, it } from "vitest";
import { singleFlight } from "./single-flight";

describe("singleFlight (UI-AUTH-8: concurrent calls share one refresh)", () => {
  it("runs once for concurrent callers with the same key and gives them the same result", async () => {
    const store = new Map<string, Promise<number>>();
    let calls = 0;
    const run = async () => {
      calls++;
      await new Promise((r) => setTimeout(r, 10));
      return calls;
    };
    const results = await Promise.all(
      Array.from({ length: 10 }, () => singleFlight(store, "rt-1", run)),
    );
    expect(calls).toBe(1);
    expect(new Set(results)).toEqual(new Set([1]));
    expect(store.size).toBe(0);
  });

  it("does not share across keys, and runs again after completion", async () => {
    const store = new Map<string, Promise<string>>();
    let calls = 0;
    const run = async () => `r${++calls}`;
    const [a, b] = await Promise.all([
      singleFlight(store, "a", run),
      singleFlight(store, "b", run),
    ]);
    expect(a).not.toBe(b);
    expect(await singleFlight(store, "a", run)).toBe("r3");
  });

  it("clears the entry when the call fails, so the next attempt can retry", async () => {
    const store = new Map<string, Promise<number>>();
    await expect(singleFlight(store, "k", () => Promise.reject(new Error("boom")))).rejects.toThrow(
      "boom",
    );
    expect(store.size).toBe(0);
    expect(await singleFlight(store, "k", async () => 7)).toBe(7);
  });
});
