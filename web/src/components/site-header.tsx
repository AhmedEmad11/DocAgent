import { useTranslations } from "next-intl";
import { Link } from "@/i18n/navigation";
import { LocaleSwitcher } from "@/components/locale-switcher";
import { ThemeToggle } from "@/components/theme-toggle";

const navItems = ["documents", "ask", "settings", "account"] as const;

export function SiteHeader() {
  const t = useTranslations();
  return (
    <header className="border-b border-border">
      <div className="mx-auto flex max-w-5xl flex-wrap items-center justify-between gap-x-4 gap-y-2 px-4 py-3">
        <Link href="/" className="text-lg font-semibold tracking-tight">
          {t("Common.wordmark")}
        </Link>
        <div className="flex items-center gap-1">
          <LocaleSwitcher />
          <ThemeToggle />
        </div>
        {/* Placeholders until the signed-in pages exist (9.1 onwards). */}
        <nav aria-label={t("Nav.label")} className="w-full sm:w-auto">
          <ul className="flex flex-wrap gap-x-4 gap-y-1 text-sm text-muted-foreground">
            {navItems.map((item) => (
              <li key={item}>
                <span aria-disabled="true" title={t("Common.comingSoon")}>
                  {t(`Nav.${item}`)}
                </span>
              </li>
            ))}
          </ul>
        </nav>
      </div>
    </header>
  );
}
