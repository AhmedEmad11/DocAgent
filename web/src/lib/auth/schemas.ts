import { z } from "zod";

// Form schemas shared by the client forms and the server actions (plan: React Hook Form + Zod).
// Issue messages are i18n keys under `Auth.errors.*`, translated where they are shown.

export const emailSchema = z
  .string()
  .trim()
  .min(1, "emailRequired")
  .max(254, "emailInvalid")
  .regex(/^[^\s@]+@[^\s@]+\.[^\s@]+$/, "emailInvalid");

/** D25: Identity defaults. The API stays the authority; this lists the rules before submit (UI-AUTH-1). */
export const passwordRules = [
  { key: "min", test: (p: string) => p.length >= 6 },
  { key: "upper", test: (p: string) => /[A-Z]/.test(p) },
  { key: "lower", test: (p: string) => /[a-z]/.test(p) },
  { key: "digit", test: (p: string) => /[0-9]/.test(p) },
  { key: "symbol", test: (p: string) => /[^A-Za-z0-9]/.test(p) },
] as const;

export type PasswordRuleKey = (typeof passwordRules)[number]["key"];

export const newPasswordSchema = z
  .string()
  .max(128, "passwordTooLong")
  .superRefine((value, ctx) => {
    if (value.length === 0) {
      ctx.addIssue({ code: "custom", message: "passwordRequired" });
      return;
    }
    for (const rule of passwordRules) {
      if (!rule.test(value)) ctx.addIssue({ code: "custom", message: `rule_${rule.key}` });
    }
  });

export const registerSchema = z.object({ email: emailSchema, password: newPasswordSchema });
export const signInSchema = z.object({
  email: emailSchema,
  password: z.string().min(1, "passwordRequired").max(128, "passwordTooLong"),
});
export const forgotSchema = z.object({ email: emailSchema });
export const resetSchema = z.object({
  email: emailSchema,
  resetCode: z.string().trim().min(1, "codeRequired").max(2048, "codeInvalid"),
  newPassword: newPasswordSchema,
});
export const resendSchema = z.object({ email: emailSchema });
export const confirmSchema = z.object({
  userId: z.string().trim().min(1).max(64),
  code: z.string().trim().min(1).max(2048),
});

export type RegisterInput = z.infer<typeof registerSchema>;
export type SignInInput = z.infer<typeof signInSchema>;
export type ForgotInput = z.infer<typeof forgotSchema>;
export type ResetInput = z.infer<typeof resetSchema>;
export type ResendInput = z.infer<typeof resendSchema>;
