import { describe, expect, it } from "vitest";
import { apiOriginFor, buildCsp } from "@/lib/csp";

describe("buildCsp", () => {
  const prod = buildCsp("abc123", false);

  it("includes the nonce and the required restrictive directives (UI-SEC-4)", () => {
    expect(prod).toContain("script-src 'self' 'nonce-abc123' 'strict-dynamic'");
    expect(prod).toContain("default-src 'self'");
    expect(prod).toContain("connect-src 'self'");
    expect(prod).toContain("object-src 'none'");
    expect(prod).toContain("base-uri 'none'");
    expect(prod).toContain("form-action 'self'");
    expect(prod).toContain("frame-ancestors 'none'");
    expect(prod).toContain("img-src 'self' data:");
  });

  it("never allows eval or inline in production", () => {
    expect(prod).not.toContain("unsafe-eval");
    expect(prod).not.toContain("unsafe-inline");
  });

  it("allows eval only in development", () => {
    expect(buildCsp("n", true)).toContain("'unsafe-eval'");
  });

  it("adds the API origin to connect-src only when asked (the upload page, plan A3)", () => {
    const csp = buildCsp("n", false, "https://api.example.test");
    expect(csp).toContain("connect-src 'self' https://api.example.test;");
    expect(prod).not.toContain("api.example.test");
  });
});

describe("apiOriginFor", () => {
  it("gives the API origin to the documents page in every locale and to nothing else", () => {
    expect(apiOriginFor("/en/documents", "https://api.example.test/")).toBe(
      "https://api.example.test",
    );
    expect(apiOriginFor("/ar/documents/", "https://api.example.test")).toBe(
      "https://api.example.test",
    );
    expect(apiOriginFor("/en/sign-in", "https://api.example.test")).toBeUndefined();
    expect(apiOriginFor("/en/documents/x", "https://api.example.test")).toBeUndefined();
    expect(apiOriginFor("/en/documents", undefined)).toBeUndefined();
    expect(apiOriginFor("/en/documents", "not a url")).toBeUndefined();
  });
});
