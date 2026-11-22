import "server-only";
import { cache } from "react";
import { headers } from "next/headers";
import { getMe, type Me } from "@/lib/api/auth";
import { readSession } from "@/lib/session/server";
import { redirect } from "@/i18n/navigation";
import type { Locale } from "@/i18n/routing";

export type CurrentUser =
  { status: "anonymous" } | { status: "unavailable" } | { status: "ok"; me: Me };

/**
 * The signed-in user for this request (UI-AUTH-9, 10). One `GET /me` per request (React `cache`). Token refresh already
 * happened in the proxy, so a 401 here means the account or tokens are gone: treated as anonymous.
 */
export const getCurrentUser = cache(async (): Promise<CurrentUser> => {
  const session = await readSession();
  if (!session) return { status: "anonymous" };
  const result = await getMe({ visitorHeaders: await headers() }, session.at);
  if (result.ok) return { status: "ok", me: result.data };
  if (result.problem.status === 401) return { status: "anonymous" };
  return { status: "unavailable" };
});

/** For pages that need a signed-in user: anonymous visitors go to sign-in and come back (UI-AUTH-9). */
export async function requireUser(locale: Locale, returnTo: string): Promise<Me> {
  const user = await getCurrentUser();
  if (user.status === "ok") return user.me;
  if (user.status === "unavailable") throw new Error("The service is not responding");
  return redirect({
    href: { pathname: "/sign-in", query: { next: `/${locale}${returnTo}` } },
    locale,
  });
}
