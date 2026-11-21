import { describe, expect, it } from "vitest";
import { buildCsp } from "@/lib/csp";

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
});
