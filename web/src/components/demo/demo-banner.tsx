import { getTranslations } from "next-intl/server";
import { Link } from "@/i18n/navigation";
import { getCurrentUser } from "@/lib/auth/current-user";
import { DemoBannerView } from "./demo-banner-view";

/**
 * Above the page content on every signed-in page (UI-DEMO-1, 4). A demo account sees the allowance banner; an account with its own key sees
 * nothing; an account with no key while the demo is off sees a hint to add one. The numbers come from `GET /me` on every render.
 */
export async function DemoBanner() {
  const user = await getCurrentUser();
  if (user.status !== "ok") return null;
  const { me } = user;
  if (me.demo) return <DemoBannerView demo={me.demo} />;
  if (!me.hasLlmKey && me.emailConfirmed) {
    const t = await getTranslations("Demo");
    return (
      <div
        role="region"
        aria-label={t("region")}
        data-testid="no-key-banner"
        className="border-b border-border bg-muted"
      >
        <p className="mx-auto max-w-5xl px-4 py-2 text-sm">
          {t("noKey")}{" "}
          <Link href="/settings" className="text-primary underline underline-offset-4">
            {t("addKey")}
          </Link>
        </p>
      </div>
    );
  }
  return null;
}
