import type { Metadata } from "next";
import { headers } from "next/headers";
import { hasLocale } from "next-intl";
import { getTranslations } from "next-intl/server";
import { notFound } from "next/navigation";
import { KeySettings } from "@/components/settings/key-settings";
import { UnconfirmedNotice } from "@/components/unconfirmed-notice";
import { routing } from "@/i18n/routing";
import { getSavedKey } from "@/lib/api/llm-key";
import { requireUser } from "@/lib/auth/current-user";
import { readSession } from "@/lib/session/server";

export async function generateMetadata({
  params,
}: PageProps<"/[locale]/settings">): Promise<Metadata> {
  const { locale } = await params;
  if (!hasLocale(routing.locales, locale)) return {};
  const t = await getTranslations({ locale, namespace: "Nav" });
  return { title: t("settings") };
}

/** LLM key settings (UI-KEY-1 to 6). The saved configuration is read on the server; the full key never leaves the API. */
export default async function SettingsPage({ params }: PageProps<"/[locale]/settings">) {
  const { locale } = await params;
  if (!hasLocale(routing.locales, locale)) notFound();
  const me = await requireUser(locale, "/settings");
  const t = await getTranslations("Settings");

  let content: React.ReactNode;
  if (!me.emailConfirmed) {
    content = <UnconfirmedNotice />;
  } else {
    const session = await readSession();
    const result = session
      ? await getSavedKey({ visitorHeaders: await headers() }, session.at)
      : null;
    if (!result || !result.ok) throw new Error("The key settings could not be loaded");
    content = <KeySettings saved={result.data} demo={Boolean(me.demo)} />;
  }

  return (
    <div className="max-w-2xl space-y-6">
      <header className="space-y-2">
        <h1 className="text-3xl font-semibold tracking-tight">{t("title")}</h1>
        <p className="text-muted-foreground">{t("intro")}</p>
      </header>
      {content}
    </div>
  );
}
