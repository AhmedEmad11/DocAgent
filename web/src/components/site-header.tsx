import { getTranslations } from "next-intl/server";
import { Link } from "@/i18n/navigation";
import { LocaleSwitcher } from "@/components/locale-switcher";
import { SignOutButton } from "@/components/auth/sign-out-button";
import { ApiPrewarm } from "@/components/readiness/api-prewarm";
import { ThemeToggle } from "@/components/theme-toggle";
import { getCurrentUser } from "@/lib/auth/current-user";

const linkClass =
  "text-muted-foreground underline-offset-4 hover:text-foreground hover:underline focus-visible:ring-[3px] focus-visible:ring-ring/50 focus-visible:outline-none";

export async function SiteHeader() {
  const t = await getTranslations();
  const user = await getCurrentUser();
  const signedIn = user.status === "ok";
  return (
    <header className="border-b border-border">
      <div className="mx-auto flex max-w-5xl flex-wrap items-center justify-between gap-x-4 gap-y-2 px-4 py-3">
        <Link href="/" className="text-lg font-semibold tracking-tight">
          {t("Common.wordmark")}
        </Link>
        <div className="flex items-center gap-1">
          <LocaleSwitcher />
          <ThemeToggle />
          {signedIn ? <SignOutButton /> : null}
          {user.status !== "anonymous" ? <ApiPrewarm /> : null}
        </div>
        <nav aria-label={t("Nav.label")} className="w-full sm:w-auto">
          <ul className="flex flex-wrap gap-x-4 gap-y-1 text-sm">
            {signedIn ? (
              <>
                <li>
                  <Link href="/documents" className={linkClass}>
                    {t("Nav.documents")}
                  </Link>
                </li>
                <li>
                  <Link href="/ask" className={linkClass}>
                    {t("Nav.ask")}
                  </Link>
                </li>
                <li>
                  <Link href="/settings" className={linkClass}>
                    {t("Nav.settings")}
                  </Link>
                </li>
                <li>
                  <Link href="/account" className={linkClass}>
                    {t("Nav.account")}
                  </Link>
                </li>
              </>
            ) : (
              <>
                <li>
                  <Link href="/sign-in" className={linkClass}>
                    {t("Auth.signIn.title")}
                  </Link>
                </li>
                <li>
                  <Link href="/register" className={linkClass}>
                    {t("Auth.register.nav")}
                  </Link>
                </li>
              </>
            )}
          </ul>
        </nav>
      </div>
    </header>
  );
}
