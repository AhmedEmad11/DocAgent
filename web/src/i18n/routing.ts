import { defineRouting } from "next-intl/routing";

export const locales = ["en", "ar"] as const;
export type Locale = (typeof locales)[number];

export const routing = defineRouting({
  locales,
  defaultLocale: "en",
  localePrefix: "always",
  // Explicit choice cookie (UI-I18N-2): holds only the locale, no personal data.
  localeCookie: { name: "NEXT_LOCALE", sameSite: "lax", maxAge: 60 * 60 * 24 * 365 },
});

const rtlLocales: readonly Locale[] = ["ar"];

export function getDirection(locale: string): "rtl" | "ltr" {
  return (rtlLocales as readonly string[]).includes(locale) ? "rtl" : "ltr";
}
