import { describe, expect, it } from "vitest";
import { z } from "zod";
import { zodResolver } from "./zod-resolver";

const schema = z.object({
  email: z.string().min(1, "emailRequired"),
  password: z.string().superRefine((v, ctx) => {
    if (v.length < 3) ctx.addIssue({ code: "custom", message: "rule_min" });
    if (!/\d/.test(v)) ctx.addIssue({ code: "custom", message: "rule_digit" });
  }),
});

describe("zodResolver", () => {
  it("returns parsed values when valid", async () => {
    const r = await zodResolver(schema)({ email: "a", password: "abc1" }, undefined, {} as never);
    expect(r).toEqual({ values: { email: "a", password: "abc1" }, errors: {} });
  });

  it("returns every failing message per field in `types`", async () => {
    const r = await zodResolver(schema)({ email: "", password: "a" }, undefined, {} as never);
    expect(r.values).toEqual({});
    expect(r.errors).toMatchObject({
      email: { message: "emailRequired", types: { emailRequired: "emailRequired" } },
      password: { types: { rule_min: "rule_min", rule_digit: "rule_digit" } },
    });
  });
});
