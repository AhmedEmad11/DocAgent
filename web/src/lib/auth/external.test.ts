import { beforeAll, describe, expect, it } from "vitest";

beforeAll(() => {
  process.env.NEXT_PUBLIC_SITE_URL = "https://ui.example";
});

import { externalStartUrl } from "@/lib/api/auth";
import { newNonce, noncesMatch } from "./external";

describe("external login state (UI-SEC-3)", () => {
  it("creates long, unique, URL-safe nonces", () => {
    const a = newNonce();
    expect(a).toMatch(/^[A-Za-z0-9_-]{40,}$/);
    expect(newNonce()).not.toBe(a);
  });

  it("compares nonces exactly", () => {
    expect(noncesMatch("abc", "abc")).toBe(true);
    expect(noncesMatch("abc", "abd")).toBe(false);
    expect(noncesMatch("abc", "abcd")).toBe(false);
    expect(noncesMatch("", "x")).toBe(false);
  });

  it("builds the API start URL with the encoded return URL carrying the nonce", () => {
    const url = externalStartUrl(
      "https://api.example",
      "google",
      "https://ui.example/api/auth/external/callback?s=N0nce",
    );
    expect(url).toBe(
      "https://api.example/api/v1/auth/external/google?returnUrl=https%3A%2F%2Fui.example%2Fapi%2Fauth%2Fexternal%2Fcallback%3Fs%3DN0nce",
    );
  });
});
