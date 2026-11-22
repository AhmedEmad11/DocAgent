import { getTranslations } from "next-intl/server";
import { buttonVariants } from "@/components/ui/button";
import type { Locale } from "@/i18n/routing";

/**
 * "Continue with Google / GitHub" (UI-AUTH-6). Plain links to the BFF start route: a browser navigation, not an XHR.
 * `next` was validated by the page; the route validates it again.
 */
export async function ExternalButtons({
  locale,
  next,
}: {
  locale: Locale;
  next?: string | undefined;
}) {
  const t = await getTranslations({ locale, namespace: "Auth" });
  const query = (provider: string) =>
    `/api/auth/external/${provider}?locale=${encodeURIComponent(locale)}${next ? `&next=${encodeURIComponent(next)}` : ""}`;
  return (
    <div className="space-y-3">
      <p className="text-center text-sm text-muted-foreground">{t("signIn.or")}</p>
      {/* Not next/link: no prefetching, and the route is outside the locale router. */}
      <a href={query("google")} className={`${buttonVariants({ variant: "outline" })} w-full`}>
        {t("signIn.google")}
      </a>
      <a href={query("github")} className={`${buttonVariants({ variant: "outline" })} w-full`}>
        {t("signIn.github")}
      </a>
    </div>
  );
}
