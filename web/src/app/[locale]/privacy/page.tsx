import type { Metadata } from "next";
import { hasLocale } from "next-intl";
import { getTranslations } from "next-intl/server";
import { routing } from "@/i18n/routing";

export async function generateMetadata({
  params,
}: PageProps<"/[locale]/privacy">): Promise<Metadata> {
  const { locale } = await params;
  if (!hasLocale(routing.locales, locale)) return {};
  const t = await getTranslations({ locale, namespace: "Privacy" });
  return { title: t("title") };
}

export default async function PrivacyPage() {
  const t = await getTranslations("Privacy");

  return (
    <article className="max-w-2xl space-y-4">
      <h1 className="text-3xl font-semibold tracking-tight">{t("title")}</h1>
      <p className="rounded-md border border-border bg-muted p-3 text-sm">{t("draftNotice")}</p>
      <h2 className="pt-2 text-xl font-semibold">{t("dataTitle")}</h2>
      <ul className="list-disc space-y-2 ps-6">
        <li>{t("demo")}</li>
        <li>{t("byok")}</li>
        <li>{t("originals")}</li>
      </ul>
      <h2 className="pt-2 text-xl font-semibold">{t("cookiesTitle")}</h2>
      <p>{t("cookies")}</p>
    </article>
  );
}
