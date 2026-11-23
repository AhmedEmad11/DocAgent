import type { Metadata } from "next";
import { hasLocale } from "next-intl";
import { getTranslations } from "next-intl/server";
import { notFound } from "next/navigation";
import { Link } from "@/i18n/navigation";
import { routing } from "@/i18n/routing";

export async function generateMetadata({
  params,
}: PageProps<"/[locale]/goodbye">): Promise<Metadata> {
  const { locale } = await params;
  if (!hasLocale(routing.locales, locale)) return {};
  const t = await getTranslations({ locale, namespace: "Goodbye" });
  return { title: t("title") };
}

/** Shown after the account was deleted (UI-ACC-3). Public: the session is already gone. */
export default async function GoodbyePage({ params }: PageProps<"/[locale]/goodbye">) {
  const { locale } = await params;
  if (!hasLocale(routing.locales, locale)) notFound();
  const t = await getTranslations("Goodbye");
  return (
    <div className="mx-auto max-w-md space-y-4">
      <h1 className="text-3xl font-semibold tracking-tight">{t("title")}</h1>
      <p>{t("body")}</p>
      <p className="text-muted-foreground">{t("again")}</p>
      <div className="flex flex-wrap gap-3">
        <Link href="/" className="text-primary underline underline-offset-4">
          {t("home")}
        </Link>
        <Link href="/register" className="text-primary underline underline-offset-4">
          {t("register")}
        </Link>
      </div>
    </div>
  );
}
