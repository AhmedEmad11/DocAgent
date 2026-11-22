import { describe, expect, it } from "vitest";
import {
  mapConfirmError,
  mapQuietError,
  mapRegisterError,
  mapResetError,
  mapSignInError,
  toExternalError,
} from "./error-map";
import ar from "../../../messages/ar.json";
import en from "../../../messages/en.json";

const identity400 = (codes: string[]) => ({
  status: 400,
  title: "One or more validation errors occurred.",
  errors: Object.fromEntries(codes.map((c) => [c, ["x"]])),
});

describe("register errors (UI-AUTH-1, SRS 10.6)", () => {
  it("lists each failed password rule next to the field", () => {
    expect(
      mapRegisterError(
        identity400([
          "PasswordRequiresUpper",
          "PasswordTooShort",
          "PasswordRequiresNonAlphanumeric",
        ]),
      ),
    ).toEqual({
      fieldErrors: { password: ["rule_upper", "rule_min", "rule_symbol"] },
    });
  });

  it("a duplicate email gives the generic message, never 'already exists'", () => {
    expect(mapRegisterError(identity400(["DuplicateUserName", "DuplicateEmail"]))).toEqual({
      formError: "registerFailed",
    });
    expect(en.Auth.errors.registerFailed).not.toMatch(/already|exists|taken/i);
  });

  it("maps an invalid email, and falls back to 'check the fields'", () => {
    expect(mapRegisterError(identity400(["InvalidEmail"]))).toEqual({
      fieldErrors: { email: ["emailInvalid"] },
    });
    expect(mapRegisterError({ status: 400, title: "x" })).toEqual({ formError: "checkFields" });
  });
});

describe("sign-in errors (UI-AUTH-4)", () => {
  it("gives wrong password and unknown account the same message", () => {
    const failed = { status: 401, title: "Unauthorized", detail: "Failed" };
    expect(mapSignInError(failed)).toEqual({ formError: "badCredentials" });
    expect(mapSignInError({ status: 401, title: "Unauthorized", detail: "NotAllowed" })).toEqual({
      formError: "badCredentials",
    });
    expect(mapSignInError({ status: 401, title: "Unauthorized" })).toEqual({
      formError: "badCredentials",
    });
  });
  it("calls out a lockout", () => {
    expect(mapSignInError({ status: 401, title: "Unauthorized", detail: "LockedOut" })).toEqual({
      formError: "lockedOut",
    });
  });
});

describe("shared cases", () => {
  it("rate limits keep Retry-After, outages say so", () => {
    expect(mapSignInError({ status: 429, title: "x", retryAfter: 12 })).toEqual({
      formError: "rateLimitedAfter",
      retryAfter: 12,
    });
    expect(mapRegisterError({ status: 429, title: "x" })).toEqual({ formError: "rateLimited" });
    expect(mapResetError({ status: 0, title: "x", code: "network" })).toEqual({
      formError: "network",
    });
    expect(mapConfirmError({ status: 503, title: "x" })).toEqual({ formError: "server" });
    expect(mapQuietError({ status: 502, title: "x" })).toEqual({ formError: "server" });
  });

  it("forgot/resend stay silent about anything but limits and outages (D29)", () => {
    expect(mapQuietError({ status: 400, title: "x" })).toBeNull();
    expect(mapQuietError({ status: 200, title: "x" })).toBeNull();
  });

  it("reset maps an invalid code and password rules", () => {
    expect(mapResetError(identity400(["InvalidToken"]))).toEqual({ formError: "resetInvalid" });
    expect(mapResetError(identity400(["PasswordRequiresDigit"]))).toEqual({
      fieldErrors: { newPassword: ["rule_digit"] },
    });
  });

  it("confirm failure is one message", () => {
    expect(mapConfirmError({ status: 401, title: "Unauthorized" })).toEqual({
      formError: "confirmInvalid",
    });
  });
});

describe("?error= values from the external callback (UI-AUTH-6)", () => {
  it("passes the three known values and replaces everything else", () => {
    expect(toExternalError("email_not_verified")).toBe("email_not_verified");
    expect(toExternalError("no_verified_email")).toBe("no_verified_email");
    expect(toExternalError("external_login_failed")).toBe("external_login_failed");
    expect(toExternalError("<script>")).toBe("external_login_failed");
    expect(toExternalError(null)).toBe("external_login_failed");
  });
});

describe("every message key the mappers can return exists in both catalogs", () => {
  const keys = [
    "badCredentials",
    "lockedOut",
    "registerFailed",
    "checkFields",
    "resetInvalid",
    "confirmInvalid",
    "network",
    "rateLimited",
    "rateLimitedAfter",
    "server",
    "forbidden",
    "emailRequired",
    "emailInvalid",
    "passwordRequired",
    "passwordTooLong",
    "codeRequired",
    "codeInvalid",
    "rule_min",
    "rule_upper",
    "rule_lower",
    "rule_digit",
    "rule_symbol",
    "rule_unique",
  ];
  for (const [name, catalog] of [
    ["en", en],
    ["ar", ar],
  ] as const) {
    it(`${name}`, () => {
      for (const k of keys) expect(catalog.Auth.errors, k).toHaveProperty(k);
    });
  }
});
