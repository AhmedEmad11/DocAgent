import { describe, expect, it } from "vitest";
import { matchAllowed } from "./allowlist";

const ID = "123e4567-e89b-12d3-a456-426614174000";
const m = (method: string, path: string) => matchAllowed(method, path.split("/").filter(Boolean));

describe("proxy allowlist (UI-SEC-7)", () => {
  it("forwards the documented routes and methods", () => {
    expect(m("GET", "/me")?.path).toBe("/me");
    expect(m("DELETE", "/me")?.path).toBe("/me");
    expect(m("PUT", "/me/llm-key")?.route.maxBodyBytes).toBeGreaterThan(0);
    expect(m("GET", "/documents")?.path).toBe("/documents");
    expect(m("GET", `/documents/${ID}`)?.path).toBe(`/documents/${ID}`);
    expect(m("DELETE", `/documents/${ID}`)).not.toBeNull();
    expect(m("POST", `/documents/${ID}/summary`)).not.toBeNull();
    expect(m("POST", "/documents/compare")).not.toBeNull();
    expect(m("POST", "/ask")).not.toBeNull();
    expect(m("GET", "/providers/openrouter/models")?.route.headers).toEqual(["x-llm-api-key"]);
  });

  it("refuses the auth endpoints, the stream, the upload and anything unknown", () => {
    for (const [method, path] of [
      ["POST", "/auth/login"],
      ["POST", "/auth/refresh"],
      ["GET", "/auth/confirmEmail"],
      ["POST", "/ask/stream"],
      ["POST", "/documents"],
      ["GET", "/health/ready"],
      ["GET", "/openapi/v1.json"],
      ["GET", "/"],
    ] as const) {
      expect(m(method, path), `${method} ${path}`).toBeNull();
    }
  });

  it("refuses wrong methods", () => {
    expect(m("POST", "/me")).toBeNull();
    expect(m("DELETE", "/documents")).toBeNull();
    expect(m("PUT", `/documents/${ID}`)).toBeNull();
    expect(m("GET", "/ask")).toBeNull();
  });

  it("refuses malformed ids and traversal tricks (no SSRF, no path escape)", () => {
    expect(m("GET", "/documents/not-a-guid")).toBeNull();
    expect(matchAllowed("GET", ["me", ".."])).toBeNull();
    expect(matchAllowed("GET", ["documents", "..", "auth", "login"])).toBeNull();
    expect(matchAllowed("GET", ["https://evil.example"])).toBeNull();
    expect(matchAllowed("GET", ["me\r\nx: y"])).toBeNull();
    expect(matchAllowed("GET", ["documents", "a/b"])).toBeNull();
    expect(matchAllowed("GET", [])).toBeNull();
    expect(matchAllowed("GET", ["a", "b", "c", "d", "e"])).toBeNull();
  });

  it("only the models route may carry the provider key header", () => {
    for (const [method, path] of [
      ["GET", "/me"],
      ["POST", "/ask"],
    ] as const) {
      expect(m(method, path)?.route.headers ?? []).not.toContain("x-llm-api-key");
    }
  });
});
