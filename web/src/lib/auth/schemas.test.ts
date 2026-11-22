import { describe, expect, it } from "vitest";
import {
  forgotSchema,
  newPasswordSchema,
  registerSchema,
  resetSchema,
  signInSchema,
} from "./schemas";

const messages = (r: { success: boolean; error?: { issues: { message: string }[] } }) =>
  r.error?.issues.map((i) => i.message) ?? [];

describe("password rules (D25)", () => {
  it("accepts a password with upper, lower, digit and symbol, 6+ characters", () => {
    expect(newPasswordSchema.safeParse("Example-Only-1").success).toBe(true);
    expect(newPasswordSchema.safeParse("Ab1!xy").success).toBe(true);
  });

  it("lists every broken rule, not just the first", () => {
    expect(messages(newPasswordSchema.safeParse("abc"))).toEqual([
      "rule_min",
      "rule_upper",
      "rule_digit",
      "rule_symbol",
    ]);
    expect(messages(newPasswordSchema.safeParse("ABCDEFG1!"))).toEqual(["rule_lower"]);
  });

  it("asks for a password when empty and caps the length", () => {
    expect(messages(newPasswordSchema.safeParse(""))).toEqual(["passwordRequired"]);
    expect(newPasswordSchema.safeParse("Aa1!" + "x".repeat(130)).success).toBe(false);
  });
});

describe("form schemas", () => {
  it("trims and validates the email", () => {
    expect(forgotSchema.parse({ email: "  a@example.com " }).email).toBe("a@example.com");
    expect(messages(forgotSchema.safeParse({ email: "" }))).toContain("emailRequired");
    for (const bad of ["a", "a@", "@b.c", "a b@c.d", "a@b"]) {
      expect(messages(forgotSchema.safeParse({ email: bad })), bad).toContain("emailInvalid");
    }
  });

  it("sign-in only needs a non-empty password (the API decides)", () => {
    expect(signInSchema.safeParse({ email: "a@example.com", password: "x" }).success).toBe(true);
    expect(messages(signInSchema.safeParse({ email: "a@example.com", password: "" }))).toEqual([
      "passwordRequired",
    ]);
  });

  it("register and reset reuse the password rules", () => {
    expect(registerSchema.safeParse({ email: "a@example.com", password: "weak" }).success).toBe(
      false,
    );
    expect(
      resetSchema.safeParse({
        email: "a@example.com",
        resetCode: "CODE",
        newPassword: "Example-Only-2",
      }).success,
    ).toBe(true);
    expect(
      messages(
        resetSchema.safeParse({
          email: "a@example.com",
          resetCode: " ",
          newPassword: "Example-Only-2",
        }),
      ),
    ).toEqual(["codeRequired"]);
  });
});
