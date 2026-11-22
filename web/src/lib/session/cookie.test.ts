import { describe, expect, it } from "vitest";
import { cookieOptions, externalStateCookieName, isSecureSite, sessionCookieName } from "./cookie";
import { removeCookieFromHeader, setCookieInHeader } from "./cookie-header";

describe("cookie flags (UI-SEC-2)", () => {
  it("uses the __Host- prefix and Secure over HTTPS (production)", () => {
    expect(sessionCookieName("https://docagent.vercel.app")).toBe("__Host-docagent_session");
    expect(externalStateCookieName("https://docagent.vercel.app")).toBe("__Host-docagent_ext");
    expect(cookieOptions("https://docagent.vercel.app", 100)).toEqual({
      httpOnly: true,
      secure: true,
      sameSite: "lax",
      path: "/",
      maxAge: 100,
    });
  });

  it("drops the prefix and Secure on plain-HTTP local development, keeping HttpOnly, Lax, Path=/", () => {
    expect(isSecureSite("http://localhost:3000")).toBe(false);
    expect(sessionCookieName("http://localhost:3000")).toBe("docagent_session");
    const o = cookieOptions("http://localhost:3000", 5);
    expect(o).toMatchObject({ httpOnly: true, secure: false, sameSite: "lax", path: "/" });
  });

  it("never sets a Domain (host-only cookie, required by __Host-)", () => {
    expect(Object.keys(cookieOptions("https://x.example", 1))).not.toContain("domain");
  });
});

describe("cookie header editing (proxy hands the refreshed session to the same request)", () => {
  it("replaces or adds one cookie and keeps the others", () => {
    expect(setCookieInHeader("a=1; s=old; b=2", "s", "new")).toBe("a=1; b=2; s=new");
    expect(setCookieInHeader(null, "s", "new")).toBe("s=new");
  });
  it("removes one cookie", () => {
    expect(removeCookieFromHeader("a=1; s=old; b=2", "s")).toBe("a=1; b=2");
    expect(removeCookieFromHeader("s=old", "s")).toBe("");
    expect(removeCookieFromHeader(null, "s")).toBe("");
  });
});
