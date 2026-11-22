import { describe, expect, it } from "vitest";
import { parseProblem } from "./problem";

const res = (status: number, body: unknown, headers: Record<string, string> = {}) =>
  new Response(typeof body === "string" ? body : JSON.stringify(body), { status, headers });

describe("parseProblem", () => {
  it("normalises Problem Details (status, title, detail, code, retryAfter)", async () => {
    const p = await parseProblem(
      res(429, {
        type: "x",
        title: "Too many requests",
        status: 429,
        detail: "Slow down",
        code: "demo_rate_limited",
        retryAfter: 12,
      }),
    );
    expect(p).toEqual({
      status: 429,
      title: "Too many requests",
      detail: "Slow down",
      code: "demo_rate_limited",
      retryAfter: 12,
    });
  });

  it("falls back to the Retry-After header", async () => {
    expect((await parseProblem(res(429, {}, { "retry-after": "7" }))).retryAfter).toBe(7);
  });

  it("keeps Identity validation errors by code", async () => {
    const p = await parseProblem(
      res(400, {
        title: "One or more validation errors occurred.",
        errors: { PasswordTooShort: ["a"], X: "not-an-array" },
      }),
    );
    expect(p.errors).toEqual({ PasswordTooShort: ["a"] });
  });

  it("never throws on empty or non-JSON bodies, and uses the status title", async () => {
    expect(await parseProblem(res(401, ""))).toEqual({ status: 401, title: "Unauthorized" });
    expect(await parseProblem(res(502, "<html>bad gateway</html>"))).toEqual({
      status: 502,
      title: "Bad gateway",
    });
    expect(await parseProblem(res(418, "[1,2]"))).toEqual({ status: 418, title: "Error" });
  });

  it("does not copy unknown fields (nothing like tokens or request bodies is passed on)", async () => {
    const p = await parseProblem(
      res(400, { title: "t", accessToken: "SECRET", requestBody: "SECRET" }),
    );
    expect(JSON.stringify(p)).not.toContain("SECRET");
  });
});
