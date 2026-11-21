import type en from "../messages/en.json";
import type { Locale } from "@/i18n/routing";
import type { formats } from "@/i18n/formats";

declare module "next-intl" {
  interface AppConfig {
    Locale: Locale;
    Messages: typeof en;
    Formats: typeof formats;
  }
}
