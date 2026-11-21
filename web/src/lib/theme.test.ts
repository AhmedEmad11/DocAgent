import { describe, expect, it } from "vitest";
import { isThemeMode, nextThemeMode } from "@/lib/theme";

describe("theme helpers", () => {
  it("cycles through all modes", () => {
    expect(nextThemeMode("system")).toBe("light");
    expect(nextThemeMode("light")).toBe("dark");
    expect(nextThemeMode("dark")).toBe("system");
  });
  it("validates stored values", () => {
    expect(isThemeMode("dark")).toBe(true);
    expect(isThemeMode("blue")).toBe(false);
    expect(isThemeMode(null)).toBe(false);
  });
});
