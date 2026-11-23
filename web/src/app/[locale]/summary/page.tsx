import type { Metadata } from "next";
import { hasLocale } from "next-intl";
import { getTranslations } from "next-intl/server";
import { notFound } from "next/navigation";
import { UnconfirmedNotice } from "@/components/unconfirmed-notice";
import { WorkflowView } from "@/components/workflow/workflow-view";
import { routing } from "@/i18n/routing";
import { requireUser } from "@/lib/auth/current-user";
import { loadDocuments } from "@/lib/documents/server";

export async function generateMetadata({
  params,
}: PageProps<"/[locale]/summary">): Promise<Metadata> {
  const { locale } = await params;
  if (!hasLocale(routing.locales, locale)) return {};
  const t = await getTranslations({ locale, namespace: "Workflow" });
  return { title: t("summary.title") };
}

/** Summary of one document with an optional focus (UI-SUM-1, 3, 4). Reached from the document rows. */
export default async function SummaryPage({
  params,
  searchParams,
}: PageProps<"/[locale]/summary">) {
  const { locale } = await params;
  if (!hasLocale(routing.locales, locale)) notFound();
  const query = await searchParams;
  const me = await requireUser(locale, "/summary");
  const t = await getTranslations("Workflow");
  const doc = typeof query.doc === "string" ? query.doc : undefined;
  const content = me.emailConfirmed ? (
    <WorkflowView
      kind="summary"
      documents={(await loadDocuments())
        .filter((d) => d.status === "Ready")
        .map((d) => ({ id: d.id, fileName: d.fileName }))}
      initialA={doc}
      demo={Boolean(me.demo)}
      resetsAt={me.demo?.resetsAt}
    />
  ) : (
    <UnconfirmedNotice />
  );
  return (
    <div className="max-w-3xl space-y-6">
      <header className="space-y-2">
        <h1 className="text-3xl font-semibold tracking-tight">{t("summary.title")}</h1>
        <p className="text-muted-foreground">{t("summary.intro")}</p>
      </header>
      {content}
    </div>
  );
}
