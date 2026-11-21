import createMiddleware from "next-intl/middleware";
import { NextRequest } from "next/server";
import { buildCsp } from "@/lib/csp";
import { routing } from "@/i18n/routing";

const handleI18n = createMiddleware(routing);

// Next.js 16 "proxy" (formerly middleware): locale routing plus the per-request CSP nonce (UI-SEC-4).
export default function proxy(request: NextRequest) {
  const nonce = btoa(crypto.randomUUID());
  const csp = buildCsp(nonce, process.env.NODE_ENV !== "production");

  const requestHeaders = new Headers(request.headers);
  requestHeaders.set("x-nonce", nonce);
  requestHeaders.set("content-security-policy", csp);

  const response = handleI18n(new NextRequest(request, { headers: requestHeaders }));
  response.headers.set("content-security-policy", csp);
  return response;
}

export const config = {
  // Everything except API routes, Next internals and files with an extension.
  matcher: ["/((?!api|_next|_vercel|.*\\..*).*)"],
};
