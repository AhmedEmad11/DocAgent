import { render } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import type { ReactElement } from "react";
import { formats } from "@/i18n/formats";
import ar from "../messages/ar.json";
import en from "../messages/en.json";
import type { Locale } from "@/i18n/routing";

const catalogs = { en, ar };

export function renderWithIntl(ui: ReactElement, locale: Locale = "en") {
  return render(
    <NextIntlClientProvider locale={locale} messages={catalogs[locale]} formats={formats}>
      {ui}
    </NextIntlClientProvider>,
  );
}
