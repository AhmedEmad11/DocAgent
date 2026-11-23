import { describe, expect, it } from "vitest";
import { formatResetTime, hoursMinutes, msUntil } from "./time";

describe("demo reset time", () => {
  it("counts down without going negative", () => {
    const now = Date.parse("2026-11-24T10:00:00Z");
    expect(msUntil("2026-11-24T12:30:00Z", now)).toBe(2.5 * 3600_000);
    expect(msUntil("2026-11-24T09:00:00Z", now)).toBe(0);
    expect(msUntil("garbage", now)).toBe(0);
  });

  it("rounds a countdown up to the minute", () => {
    expect(hoursMinutes(0)).toEqual({ hours: 0, minutes: 0 });
    expect(hoursMinutes(59_001)).toEqual({ hours: 0, minutes: 1 });
    expect(hoursMinutes(2.5 * 3600_000)).toEqual({ hours: 2, minutes: 30 });
    expect(hoursMinutes(3600_000 + 1)).toEqual({ hours: 1, minutes: 1 });
  });

  it("formats the clock time in the given zone with Western digits, also in Arabic", () => {
    expect(formatResetTime("2026-11-24T18:05:00Z", "en", "UTC")).toMatch(/6:05\s?PM/);
    expect(formatResetTime("2026-11-24T18:05:00Z", "en", "Asia/Tokyo")).toMatch(/3:05\s?AM/);
    const ar = formatResetTime("2026-11-24T18:05:00Z", "ar", "UTC");
    expect(ar).toMatch(/6:05/);
    expect(ar).not.toMatch(/[٠-٩]/);
    expect(formatResetTime("nope", "en")).toBe("");
  });
});
