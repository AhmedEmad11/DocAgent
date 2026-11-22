import { z } from "zod";
import { apiJson, apiVoid, type ApiRequestInit, type ApiResult } from "./client";

type Visitor = Pick<ApiRequestInit, "visitorHeaders">;

const tokenSchema = z.object({
  tokenType: z.string(),
  accessToken: z.string().min(1),
  expiresIn: z.number().positive(),
  refreshToken: z.string().min(1),
});
export type TokenResponse = z.infer<typeof tokenSchema>;

const meSchema = z.object({
  id: z.string(),
  email: z.string().nullable(),
  emailConfirmed: z.boolean(),
  externalLogins: z.array(z.string()),
  hasLlmKey: z.boolean(),
  demo: z
    .object({
      limit: z.number(),
      used: z.number(),
      remaining: z.number(),
      resetsAt: z.string(),
      privacyNotice: z.string(),
    })
    .nullish(),
  chunks: z.object({ used: z.number(), limit: z.number() }),
});
export type Me = z.infer<typeof meSchema>;

export const register = (v: Visitor, email: string, password: string) =>
  apiVoid("/auth/register", { ...v, method: "POST", json: { email, password } });

export const login = (
  v: Visitor,
  email: string,
  password: string,
): Promise<ApiResult<TokenResponse>> =>
  apiJson("/auth/login", { ...v, method: "POST", json: { email, password } }, (b) =>
    tokenSchema.parse(b),
  );

export const refresh = (v: Visitor, refreshToken: string): Promise<ApiResult<TokenResponse>> =>
  apiJson("/auth/refresh", { ...v, method: "POST", json: { refreshToken } }, (b) =>
    tokenSchema.parse(b),
  );

export const exchangeExternalCode = (v: Visitor, code: string): Promise<ApiResult<TokenResponse>> =>
  apiJson("/auth/external/exchange", { ...v, method: "POST", json: { code } }, (b) =>
    tokenSchema.parse(b),
  );

export const resendConfirmation = (v: Visitor, email: string) =>
  apiVoid("/auth/resendConfirmationEmail", { ...v, method: "POST", json: { email } });

export const forgotPassword = (v: Visitor, email: string) =>
  apiVoid("/auth/forgotPassword", { ...v, method: "POST", json: { email } });

export const resetPassword = (v: Visitor, email: string, resetCode: string, newPassword: string) =>
  apiVoid("/auth/resetPassword", { ...v, method: "POST", json: { email, resetCode, newPassword } });

export const confirmEmail = (v: Visitor, userId: string, code: string) =>
  apiVoid(
    `/auth/confirmEmail?userId=${encodeURIComponent(userId)}&code=${encodeURIComponent(code)}`,
    {
      ...v,
      method: "GET",
    },
  );

export const getMe = (v: Visitor, accessToken: string): Promise<ApiResult<Me>> =>
  apiJson("/me", { ...v, accessToken }, (b) => meSchema.parse(b));

/** Where the API starts the external flow; the browser is sent here by our own route (never a user-supplied URL). */
export function externalStartUrl(
  apiBase: string,
  provider: "google" | "github",
  returnUrl: string,
): string {
  return `${apiBase}/api/v1/auth/external/${provider}?returnUrl=${encodeURIComponent(returnUrl)}`;
}
