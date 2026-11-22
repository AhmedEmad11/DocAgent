import { describe, expect, it } from "vitest";
import { safeNextPath } from "./next-path";

describe("safeNextPath (UI-SEC-10: only a relative path on this site)", () => {
  it("keeps same-site paths, with query, and the explicit locale", () => {
    expect(safeNextPath("/en/documents", "en")).toBe("/en/documents");
    expect(safeNextPath("/ar/privacy", "en")).toBe("/ar/privacy");
    expect(safeNextPath("/en/documents?x=1&y=2", "en")).toBe("/en/documents?x=1&y=2");
  });

  it("adds the current locale to a path without one", () => {
    expect(safeNextPath("/documents", "ar")).toBe("/ar/documents");
    expect(safeNextPath("/", "ar")).toBe("/ar");
  });

  it.each([
    "https://evil.example/",
    "http://evil.example",
    "//evil.example/path",
    "///evil.example",
    "/\\evil.example",
    "\\\\evil.example",
    "javascript:alert(1)",
    "data:text/html,x",
    "evil.example",
    "documents",
    "/en/../../etc/passwd",
    "/en/%2e%2e/x",
    "/en/documents\r\nSet-Cookie: x=1",
    "/en/documents\u0000",
    "/en/ documents\t",
  ])("rejects %j and falls back to the app page", (raw) => {
    expect(safeNextPath(raw, "en")).toBe("/en/documents");
  });

  it("falls back for empty, missing and very long values", () => {
    expect(safeNextPath(null, "ar")).toBe("/ar/documents");
    expect(safeNextPath(undefined, "en")).toBe("/en/documents");
    expect(safeNextPath("", "en")).toBe("/en/documents");
    expect(safeNextPath("/" + "a".repeat(600), "en")).toBe("/en/documents");
  });

  it("never loops back into the auth pages or the BFF routes", () => {
    for (const p of [
      "sign-in",
      "register",
      "check-email",
      "confirm",
      "forgot-password",
      "reset-password",
    ]) {
      expect(safeNextPath(`/en/${p}`, "en")).toBe("/en/documents");
    }
    expect(safeNextPath("/api/proxy/me", "en")).toBe("/en/documents");
    expect(safeNextPath("/en/api/x", "en")).toBe("/en/documents");
  });
});
