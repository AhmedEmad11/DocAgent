"use server";

import { cookies, headers } from "next/headers";
import { redirect } from "next/navigation";
import { hasLocale } from "next-intl";
import * as authApi from "@/lib/api/auth";
import {
  mapConfirmError,
  mapQuietError,
  mapRegisterError,
  mapResetError,
  mapSignInError,
} from "@/lib/auth/error-map";
import { isSameOrigin } from "@/lib/auth/origin";
import { safeNextPath } from "@/lib/auth/next-path";
import {
  confirmSchema,
  forgotSchema,
  registerSchema,
  resendSchema,
  resetSchema,
  signInSchema,
} from "@/lib/auth/schemas";
import type { ActionResult } from "@/lib/auth/actions-types";
import { getServerEnv } from "@/lib/env";
import { cookieOptions, pendingEmailCookieName } from "@/lib/session/cookie";
import { toSessionData } from "@/lib/session/refresh";
import { seal, unseal } from "@/lib/session/seal";
import { authedApiFetch, clearSession, sessionSecrets, writeSession } from "@/lib/session/server";
import { routing, type Locale } from "@/i18n/routing";

// Server actions for the auth forms (D35). Each re-validates its input with the same zod schema as the client form,
// checks the Origin (UI-SEC-3) and returns message keys only (never API text, tokens or the submitted password).

const PENDING_TTL_SECONDS = 60 * 60;

function asLocale(value: unknown): Locale {
  return typeof value === "string" && hasLocale(routing.locales, value)
    ? value
    : routing.defaultLocale;
}

async function guard(): Promise<{ visitor: { visitorHeaders: Headers } } | ActionResult> {
  const h = await headers();
  if (!isSameOrigin(h, getServerEnv().NEXT_PUBLIC_SITE_URL))
    return { ok: false, formError: "forbidden" };
  return { visitor: { visitorHeaders: h } };
}

function isFailure(g: Awaited<ReturnType<typeof guard>>): g is ActionResult {
  return "ok" in g;
}

function zodFailure(error: { issues: { path: PropertyKey[]; message: string }[] }): ActionResult {
  const fieldErrors: Record<string, string[]> = {};
  for (const issue of error.issues) {
    const field = String(issue.path[0] ?? "form");
    (fieldErrors[field] ??= []).push(issue.message);
  }
  return { ok: false, fieldErrors };
}

export async function signInAction(input: {
  email: string;
  password: string;
  locale: string;
  next?: string | null;
}): Promise<ActionResult> {
  const g = await guard();
  if (isFailure(g)) return g;
  const parsed = signInSchema.safeParse(input);
  if (!parsed.success) return zodFailure(parsed.error);

  const result = await authApi.login(g.visitor, parsed.data.email, parsed.data.password);
  if (!result.ok) return { ok: false, ...mapSignInError(result.problem) };
  await writeSession(toSessionData(result.data));
  return { ok: true, next: safeNextPath(input.next, asLocale(input.locale)) };
}

export async function registerAction(input: {
  email: string;
  password: string;
  locale: string;
}): Promise<ActionResult> {
  const g = await guard();
  if (isFailure(g)) return g;
  const parsed = registerSchema.safeParse(input);
  if (!parsed.success) return zodFailure(parsed.error);

  const result = await authApi.register(g.visitor, parsed.data.email, parsed.data.password);
  if (!result.ok) return { ok: false, ...mapRegisterError(result.problem) };

  // Remember the address for the "check your email" page and its resend button (short-lived, sealed, httpOnly).
  const siteUrl = getServerEnv().NEXT_PUBLIC_SITE_URL;
  const jar = await cookies();
  jar.set(
    pendingEmailCookieName(siteUrl),
    await seal(
      { email: parsed.data.email },
      "pending-email",
      sessionSecrets(),
      PENDING_TTL_SECONDS,
    ),
    cookieOptions(siteUrl, PENDING_TTL_SECONDS),
  );
  return { ok: true, next: `/${asLocale(input.locale)}/check-email` };
}

export async function resendConfirmationAction(input: {
  email?: string;
  locale: string;
}): Promise<ActionResult> {
  const g = await guard();
  if (isFailure(g)) return g;

  let email = input.email;
  if (!email) {
    const siteUrl = getServerEnv().NEXT_PUBLIC_SITE_URL;
    const raw = (await cookies()).get(pendingEmailCookieName(siteUrl))?.value;
    const payload = raw ? await unseal(raw, "pending-email", sessionSecrets()) : null;
    email = typeof payload?.email === "string" ? payload.email : undefined;
  }
  if (!email) {
    // A signed-in, unconfirmed user: ask the API who they are (the token stays on the server).
    const me = await authedApiFetch("/me");
    if (me.kind === "response" && me.res.ok) {
      const body = (await me.res.json()) as { email?: unknown };
      email = typeof body.email === "string" ? body.email : undefined;
    } else if (me.kind === "response") {
      await me.res.body?.cancel();
    }
  }
  const parsed = resendSchema.safeParse({ email });
  if (!parsed.success) return zodFailure(parsed.error);

  const result = await authApi.resendConfirmation(g.visitor, parsed.data.email);
  const error = result.ok ? null : mapQuietError(result.problem);
  return error ? { ok: false, ...error } : { ok: true }; // always succeeds (D29)
}

export async function forgotPasswordAction(input: {
  email: string;
  locale: string;
}): Promise<ActionResult> {
  const g = await guard();
  if (isFailure(g)) return g;
  const parsed = forgotSchema.safeParse(input);
  if (!parsed.success) return zodFailure(parsed.error);

  const result = await authApi.forgotPassword(g.visitor, parsed.data.email);
  const error = result.ok ? null : mapQuietError(result.problem);
  return error ? { ok: false, ...error } : { ok: true }; // same answer whether or not the account exists (UI-AUTH-5)
}

export async function resetPasswordAction(input: {
  email: string;
  resetCode: string;
  newPassword: string;
  locale: string;
}): Promise<ActionResult> {
  const g = await guard();
  if (isFailure(g)) return g;
  const parsed = resetSchema.safeParse(input);
  if (!parsed.success) return zodFailure(parsed.error);

  const result = await authApi.resetPassword(
    g.visitor,
    parsed.data.email,
    parsed.data.resetCode,
    parsed.data.newPassword,
  );
  if (!result.ok) return { ok: false, ...mapResetError(result.problem) };
  return { ok: true, next: `/${asLocale(input.locale)}/sign-in?reset=1` };
}

export async function confirmEmailAction(input: {
  userId: string;
  code: string;
}): Promise<ActionResult> {
  const g = await guard();
  if (isFailure(g)) return g;
  const parsed = confirmSchema.safeParse(input);
  if (!parsed.success) return { ok: false, formError: "confirmInvalid" };

  const result = await authApi.confirmEmail(g.visitor, parsed.data.userId, parsed.data.code);
  return result.ok ? { ok: true } : { ok: false, ...mapConfirmError(result.problem) };
}

/** Plain form action: works without client JavaScript. No GET endpoint signs out (UI-SEC-3). */
export async function signOutAction(formData: FormData): Promise<void> {
  const locale = asLocale(formData.get("locale"));
  const g = await guard();
  if (!isFailure(g)) await clearSession();
  redirect(`/${locale}`);
}

/**
 * Deletes the account (UI-ACC-3, AUTH-8): the API removes the user with all documents, chunks, the stored key and demo usage, then the
 * session cookie is cleared. The typed-email confirmation is checked in the dialog; the API call itself only needs the session.
 */
export async function deleteAccountAction(input: { locale: string }): Promise<ActionResult> {
  const g = await guard();
  if (isFailure(g)) return g;
  const result = await authedApiFetch("/me", { method: "DELETE" });
  if (result.kind === "anonymous") return { ok: false, formError: "sessionEnded" };
  if (result.kind === "network") return { ok: false, formError: "apiStarting" };
  const { res } = result;
  await res.body?.cancel();
  if (res.status === 503) return { ok: false, formError: "apiStarting" };
  if (res.status === 429) return { ok: false, formError: "rateLimited" };
  if (!res.ok) return { ok: false, formError: "server" };
  await clearSession();
  return { ok: true, next: `/${asLocale(input.locale)}/goodbye` };
}
