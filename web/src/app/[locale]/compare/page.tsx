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
}: PageProps<"/[locale]/compare">): Promise<Metadata> {
  const { locale } = await params;
  if (!hasLocale(routing.locales, locale)) return {};
  const t = await getTranslations({ locale, namespace: "Workflow" });
  return { title: t("compare.title") };
}

/** Comparison of two different documents with an optional focus (UI-SUM-2 to 4). Reached from the document rows (`?a=`). */
export default async function ComparePage({
  params,
  searchParams,
}: PageProps<"/[locale]/compare">) {
  const { locale } = await params;
  if (!hasLocale(routing.locales, locale)) notFound();
  const query = await searchParams;
  const me = await requireUser(locale, "/compare");
  const t = await getTranslations("Workflow");
  const first = typeof query.a === "string" ? query.a : undefined;
  const content = me.emailConfirmed ? (
    <WorkflowView
      kind="compare"
      documents={(await loadDocuments())
        .filter((d) => d.status === "Ready")
        .map((d) => ({ id: d.id, fileName: d.fileName }))}
      initialA={first}
      demo={Boolean(me.demo)}
      quota={me.demo ? { remaining: me.demo.remaining, resetsAt: me.demo.resetsAt } : null}
    />
  ) : (
    <UnconfirmedNotice />
  );
  return (
    <div className="max-w-3xl space-y-6">
      <header className="space-y-2">
        <h1 className="text-3xl font-semibold tracking-tight">{t("compare.title")}</h1>
        <p className="text-muted-foreground">{t("compare.intro")}</p>
      </header>
      {content}
    </div>
  );
}
