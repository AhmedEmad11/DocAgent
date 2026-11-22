import { describe, expect, it } from "vitest";
import { isSameOrigin } from "./origin";

const site = "https://docagent.vercel.app";
const h = (o: Record<string, string>) => ({ get: (n: string) => o[n.toLowerCase()] ?? null });

describe("isSameOrigin (UI-SEC-3)", () => {
  it("accepts the site's own origin", () => {
    expect(isSameOrigin(h({ origin: "https://docagent.vercel.app" }), site)).toBe(true);
    expect(isSameOrigin(h({ origin: "https://docagent.vercel.app" }), `${site}/some/path`)).toBe(
      true,
    );
  });

  it("rejects other origins, schemes, ports and look-alikes", () => {
    for (const origin of [
      "https://evil.example",
      "http://docagent.vercel.app",
      "https://docagent.vercel.app:8443",
      "https://docagent.vercel.app.evil.example",
      "null",
      "",
    ]) {
      expect(isSameOrigin(h({ origin }), site), origin).toBe(false);
    }
  });

  it("falls back to Sec-Fetch-Site only when there is no Origin", () => {
    expect(isSameOrigin(h({ "sec-fetch-site": "same-origin" }), site)).toBe(true);
    expect(isSameOrigin(h({ "sec-fetch-site": "same-site" }), site)).toBe(false);
    expect(isSameOrigin(h({ "sec-fetch-site": "cross-site" }), site)).toBe(false);
    expect(isSameOrigin(h({ "sec-fetch-site": "none" }), site)).toBe(false);
    // A wrong Origin wins over a (forged) Sec-Fetch-Site.
    expect(
      isSameOrigin(h({ origin: "https://evil.example", "sec-fetch-site": "same-origin" }), site),
    ).toBe(false);
  });

  it("rejects when neither header is present", () => {
    expect(isSameOrigin(h({}), site)).toBe(false);
  });
});
