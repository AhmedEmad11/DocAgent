"use client";

import { useLocale, useTranslations } from "next-intl";
import { Link, usePathname } from "@/i18n/navigation";
import { locales } from "@/i18n/routing";

export function LocaleSwitcher() {
  const t = useTranslations("Language");
  const locale = useLocale();
  const pathname = usePathname();
  const other = locales.find((l) => l !== locale) ?? locale;
  const name = t(other);

  return (
    <Link
      href={pathname}
      locale={other}
      lang={other}
      hrefLang={other}
      aria-label={t("switchTo", { name })}
      className="inline-flex min-h-9 min-w-9 items-center justify-center rounded-md px-3 text-sm font-medium hover:bg-accent hover:text-accent-foreground focus-visible:ring-[3px] focus-visible:ring-ring/50 focus-visible:outline-none"
    >
      {name}
    </Link>
  );
}
