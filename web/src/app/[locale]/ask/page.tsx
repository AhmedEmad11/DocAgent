import type { Metadata } from "next";
import { headers } from "next/headers";
import { hasLocale } from "next-intl";
import { getTranslations } from "next-intl/server";
import { notFound } from "next/navigation";
import { Notice } from "@/components/auth/auth-card";
import { ResendForm } from "@/components/auth/resend-form";
import { AskView } from "@/components/ask/ask-view";
import { routing } from "@/i18n/routing";
import { listDocuments } from "@/lib/api/documents";
import { requireUser } from "@/lib/auth/current-user";
import { readSession } from "@/lib/session/server";

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
  const td = await getTranslations("Documents");

  let content: React.ReactNode;
  if (!me.emailConfirmed) {
    content = (
      <div className="space-y-3" data-testid="unconfirmed">
        <Notice tone="error">
          <p className="font-medium">{td("unconfirmedTitle")}</p>
          <p>{td("unconfirmedBody")}</p>
        </Notice>
        <ResendForm knownEmail />
      </div>
    );
  } else {
    const session = await readSession();
    const result = session
      ? await listDocuments({ visitorHeaders: await headers() }, session.at)
      : null;
    if (!result || !result.ok) throw new Error("The documents could not be loaded");
    const wanted = [query.doc].flat().filter((d): d is string => typeof d === "string");
    content = (
      <AskView
        documents={result.data.map((d) => ({ id: d.id, fileName: d.fileName, status: d.status }))}
        initialSelected={wanted}
      />
    );
  }

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
