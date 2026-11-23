import type { Metadata } from "next";
import { hasLocale } from "next-intl";
import { getTranslations } from "next-intl/server";
import { notFound } from "next/navigation";
import { AskView } from "@/components/ask/ask-view";
import { UnconfirmedNotice } from "@/components/unconfirmed-notice";
import { routing } from "@/i18n/routing";
import { requireUser } from "@/lib/auth/current-user";
import { loadDocuments } from "@/lib/documents/server";

export async function generateMetadata({ params }: PageProps<"/[locale]/ask">): Promise<Metadata> {
  const { locale } = await params;
  if (!hasLocale(routing.locales, locale)) return {};
  const t = await getTranslations({ locale, namespace: "Nav" });
  return { title: t("ask") };
}

/** Ask (UI-QA-1 to 12): the document list is read on the server for the scope selector; the question itself streams through the BFF. */
export default async function AskPage({ params, searchParams }: PageProps<"/[locale]/ask">) {
  const { locale } = await params;
  if (!hasLocale(routing.locales, locale)) notFound();
  const query = await searchParams;
  const me = await requireUser(locale, "/ask");
  const t = await getTranslations("Ask");

  const wanted = [query.doc].flat().filter((d): d is string => typeof d === "string");
  const content = me.emailConfirmed ? (
    <AskView
      documents={(await loadDocuments()).map((d) => ({
        id: d.id,
        fileName: d.fileName,
        status: d.status,
      }))}
      initialSelected={wanted}
    />
  ) : (
    <UnconfirmedNotice />
  );

  return (
    <div className="max-w-3xl space-y-6">
      <header className="space-y-2">
        <h1 className="text-3xl font-semibold tracking-tight">{t("title")}</h1>
        <p className="text-muted-foreground">{t("intro")}</p>
      </header>
      {content}
    </div>
  );
}
