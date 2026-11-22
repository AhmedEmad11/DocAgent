import createMiddleware from "next-intl/middleware";
import { NextRequest } from "next/server";
import { buildCsp } from "@/lib/csp";
import { cookieOptions } from "@/lib/session/cookie";
import { removeCookieFromHeader, setCookieInHeader } from "@/lib/session/cookie-header";
import { SESSION_MAX_AGE_SECONDS } from "@/lib/session/model";
import { refreshForRequest } from "@/lib/session/proxy-refresh";
import { routing } from "@/i18n/routing";

const handleI18n = createMiddleware(routing);

// Next.js 16 "proxy" (formerly middleware): session refresh, locale routing and the per-request CSP nonce (UI-SEC-4).
export default async function proxy(request: NextRequest) {
  const nonce = btoa(crypto.randomUUID());
  const csp = buildCsp(nonce, process.env.NODE_ENV !== "production");

  const requestHeaders = new Headers(request.headers);
  requestHeaders.set("x-nonce", nonce);
  requestHeaders.set("content-security-policy", csp);

  const refresh = await refreshForRequest(request);
  if (refresh.action === "set") {
    requestHeaders.set(
      "cookie",
      setCookieInHeader(request.headers.get("cookie"), refresh.name, refresh.value),
    );
  } else if (refresh.action === "clear") {
    requestHeaders.set(
      "cookie",
      removeCookieFromHeader(request.headers.get("cookie"), refresh.name),
    );
  }

  const response = handleI18n(new NextRequest(request, { headers: requestHeaders }));
  response.headers.set("content-security-policy", csp);
  if (refresh.action === "set") {
    response.cookies.set(
      refresh.name,
      refresh.value,
      cookieOptions(
        process.env.NEXT_PUBLIC_SITE_URL ?? "http://localhost",
        SESSION_MAX_AGE_SECONDS,
      ),
    );
  } else if (refresh.action === "clear") {
    response.cookies.delete(refresh.name);
  }
  return response;
}

export const config = {
  // Everything except API routes, Next internals and files with an extension.
  matcher: ["/((?!api|_next|_vercel|.*\\..*).*)"],
};
