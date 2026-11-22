import { randomBytes, timingSafeEqual } from "node:crypto";
import { NextResponse, type NextRequest } from "next/server";
import { hasLocale } from "next-intl";
import { exchangeExternalCode, externalStartUrl } from "@/lib/api/auth";
import { safeNextPath } from "@/lib/auth/next-path";
import { toExternalError } from "@/lib/auth/error-map";
import { getServerEnv } from "@/lib/env";
import { cookieOptions, externalStateCookieName, sessionCookieName } from "@/lib/session/cookie";
import { SESSION_MAX_AGE_SECONDS, sealSessionData } from "@/lib/session/model";
import { toSessionData } from "@/lib/session/refresh";
import { seal, unseal, type Secrets } from "@/lib/session/seal";
import { routing, type Locale } from "@/i18n/routing";

// External sign-in (D36, UI-SEC-3, UI-SEC-5). Start: put a one-time nonce in a short-lived sealed httpOnly cookie and in the
// `returnUrl` query; send the browser to the API. Callback: check the nonce, exchange the code server to server, set the
// session, and answer with a redirect so the code never stays in the URL, history or a referrer.

export const EXTERNAL_PROVIDERS = ["google", "github"] as const;
export type ExternalProvider = (typeof EXTERNAL_PROVIDERS)[number];
export const STATE_TTL_SECONDS = 10 * 60;

const NO_LEAK = { "cache-control": "no-store", "referrer-policy": "no-referrer" } as const;

function secrets(): Secrets {
  const env = getServerEnv();
  return { current: env.SESSION_SECRET, previous: env.SESSION_SECRET_PREVIOUS };
}

function localeOf(value: unknown): Locale {
  return typeof value === "string" && hasLocale(routing.locales, value)
    ? value
    : routing.defaultLocale;
}

function redirectTo(path: string, init?: { headers?: Record<string, string> }): NextResponse {
  const res = NextResponse.redirect(new URL(path, getServerEnv().NEXT_PUBLIC_SITE_URL), 303);
  for (const [k, v] of Object.entries({ ...NO_LEAK, ...init?.headers })) res.headers.set(k, v);
  return res;
}

export function newNonce(): string {
  return randomBytes(32).toString("base64url");
}

export function noncesMatch(a: string, b: string): boolean {
  const x = Buffer.from(a);
  const y = Buffer.from(b);
  return x.length === y.length && timingSafeEqual(x, y);
}

export async function startExternal(req: NextRequest, provider: string): Promise<NextResponse> {
  if (!(EXTERNAL_PROVIDERS as readonly string[]).includes(provider)) {
    return NextResponse.json(
      { status: 404, title: "Not found" },
      { status: 404, headers: NO_LEAK },
    );
  }
  const env = getServerEnv();
  const locale = localeOf(req.nextUrl.searchParams.get("locale"));
  const next = safeNextPath(req.nextUrl.searchParams.get("next"), locale);
  const nonce = newNonce();

  const returnUrl = `${env.NEXT_PUBLIC_SITE_URL.replace(/\/$/, "")}/api/auth/external/callback?s=${nonce}`;
  const res = NextResponse.redirect(
    externalStartUrl(env.DOCAGENT_API_URL, provider as ExternalProvider, returnUrl),
    303,
  );
  for (const [k, v] of Object.entries(NO_LEAK)) res.headers.set(k, v);
  res.cookies.set(
    externalStateCookieName(env.NEXT_PUBLIC_SITE_URL),
    await seal({ n: nonce, next, locale }, "external-state", secrets(), STATE_TTL_SECONDS),
    cookieOptions(env.NEXT_PUBLIC_SITE_URL, STATE_TTL_SECONDS),
  );
  return res;
}

export async function finishExternal(req: NextRequest): Promise<NextResponse> {
  const env = getServerEnv();
  const stateName = externalStateCookieName(env.NEXT_PUBLIC_SITE_URL);
  const raw = req.cookies.get(stateName)?.value;
  const state = raw ? await unseal(raw, "external-state", secrets()) : null;
  const locale = localeOf(state?.locale);

  const fail = (error: string): NextResponse => {
    const res = redirectTo(`/${locale}/sign-in?error=${encodeURIComponent(error)}`);
    res.cookies.delete(stateName);
    return res;
  };

  const params = req.nextUrl.searchParams;
  const nonce = params.get("s");
  // Login CSRF: the callback must belong to a flow this browser started.
  if (!state || typeof state.n !== "string" || !nonce || !noncesMatch(state.n, nonce)) {
    return fail("external_login_failed");
  }
  const providerError = params.get("error");
  if (providerError) return fail(toExternalError(providerError));
  const code = params.get("code");
  if (!code || code.length > 512) return fail("external_login_failed");

  const result = await exchangeExternalCode({ visitorHeaders: req.headers }, code);
  if (!result.ok) return fail("external_login_failed");

  const res = redirectTo(safeNextPath(typeof state.next === "string" ? state.next : null, locale));
  res.cookies.delete(stateName);
  res.cookies.set(
    sessionCookieName(env.NEXT_PUBLIC_SITE_URL),
    await sealSessionData(toSessionData(result.data), secrets()),
    cookieOptions(env.NEXT_PUBLIC_SITE_URL, SESSION_MAX_AGE_SECONDS),
  );
  return res;
}
