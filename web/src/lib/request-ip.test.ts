import { describe, expect, it } from "vitest";
import { getClientIp } from "./request-ip";

const h = (o: Record<string, string>) => ({ get: (n: string) => o[n.toLowerCase()] ?? null });

describe("getClientIp (D41)", () => {
  it("takes the left-most valid address", () => {
    expect(getClientIp(h({ "x-forwarded-for": "203.0.113.7, 10.0.0.1" }))).toBe("203.0.113.7");
    expect(getClientIp(h({ "x-forwarded-for": "2001:db8::1" }))).toBe("2001:db8::1");
    expect(getClientIp(h({ "x-real-ip": "198.51.100.4" }))).toBe("198.51.100.4");
  });
  it("prefers the platform header over x-forwarded-for", () => {
    expect(
      getClientIp(h({ "x-vercel-forwarded-for": "203.0.113.9", "x-forwarded-for": "1.2.3.4" })),
    ).toBe("203.0.113.9");
  });
  it("returns undefined for nothing or garbage (the API then falls back to the connection address)", () => {
    expect(getClientIp(h({}))).toBeUndefined();
    expect(getClientIp(h({ "x-forwarded-for": "not-an-ip" }))).toBeUndefined();
    expect(getClientIp(h({ "x-forwarded-for": "evil\r\nx: y" }))).toBeUndefined();
  });
});
