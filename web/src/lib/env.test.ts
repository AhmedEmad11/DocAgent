import { describe, expect, it } from "vitest";
import { parseServerEnv } from "@/lib/env";

const valid = {
  DOCAGENT_API_URL: "https://localhost:7090",
  SESSION_SECRET: "x".repeat(32),
  NEXT_PUBLIC_SITE_URL: "http://localhost:3000",
  BFF_SHARED_SECRET: "y".repeat(16),
};

describe("parseServerEnv", () => {
  it("accepts a valid environment", () => {
    expect(parseServerEnv(valid).DOCAGENT_API_URL).toBe("https://localhost:7090");
  });

  it("rejects a short session secret without echoing it", () => {
    expect(() => parseServerEnv({ ...valid, SESSION_SECRET: "short-secret-value" })).toThrow(
      /SESSION_SECRET/,
    );
    try {
      parseServerEnv({ ...valid, SESSION_SECRET: "short-secret-value" });
    } catch (e) {
      expect(String(e)).not.toContain("short-secret-value");
    }
  });

  it("rejects a trailing slash and a missing URL", () => {
    expect(() => parseServerEnv({ ...valid, DOCAGENT_API_URL: "https://x.test/" })).toThrow();
    expect(() => parseServerEnv({ ...valid, DOCAGENT_API_URL: undefined })).toThrow();
  });
});
