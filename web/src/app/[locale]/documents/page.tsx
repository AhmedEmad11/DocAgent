import type { Metadata } from "next";
import { headers } from "next/headers";
import { hasLocale } from "next-intl";
import { getTranslations } from "next-intl/server";
import { notFound } from "next/navigation";
import { Notice } from "@/components/auth/auth-card";
import { ResendForm } from "@/components/auth/resend-form";
import { DocumentsView } from "@/components/documents/documents-view";
import { routing } from "@/i18n/routing";
import { listDocuments } from "@/lib/api/documents";
import { requireUser } from "@/lib/auth/current-user";
import { readSession } from "@/lib/session/server";

export async function generateMetadata({
  params,
}: PageProps<"/[locale]/documents">): Promise<Metadata> {
  const { locale } = await params;
  if (!hasLocale(routing.locales, locale)) return {};
  const t = await getTranslations({ locale, namespace: "Nav" });
  return { title: t("documents") };
}

/** Documents (UI-DOC-1 to 10): the first list is rendered on the server, then the browser keeps it fresh through the allowlisted proxy. */
export default async function DocumentsPage({ params }: PageProps<"/[locale]/documents">) {
  const { locale } = await params;
  if (!hasLocale(routing.locales, locale)) notFound();
  const me = await requireUser(locale, "/documents");
  const t = await getTranslations("Documents");
  const ta = await getTranslations("Account");

  let content: React.ReactNode;
  if (!me.emailConfirmed) {
    content = (
      <div className="space-y-3" data-testid="unconfirmed">
        <Notice tone="error">
          <p className="font-medium">{t("unconfirmedTitle")}</p>
          <p>{t("unconfirmedBody")}</p>
        </Notice>
        <ResendForm knownEmail />
        <p className="text-sm">
          <a href={`/${locale}/documents`} className="text-primary underline underline-offset-4">
            {t("checkAgain")}
          </a>
        </p>
      </div>
    );
  } else {
    const session = await readSession();
    const result = session
      ? await listDocuments({ visitorHeaders: await headers() }, session.at)
      : null;
    if (!result || !result.ok) throw new Error("The documents could not be loaded");
    content = <DocumentsView initial={result.data} hasLlmKey={me.hasLlmKey} chunks={me.chunks} />;
  }

  return (
    <div className="max-w-3xl space-y-6">
      <header className="space-y-2">
        <h1 className="text-3xl font-semibold tracking-tight">{t("title")}</h1>
        <p className="text-muted-foreground">{t("intro")}</p>
        <dl className="flex flex-wrap gap-x-6 gap-y-1 text-sm">
          <div className="flex gap-1.5">
            <dt className="text-muted-foreground">{ta("email")}</dt>
            <dd dir="ltr" className="font-medium" data-testid="account-email">
              {me.email}
            </dd>
          </div>
          <div className="flex gap-1.5">
            <dt className="text-muted-foreground">{ta("methods")}</dt>
            <dd data-testid="account-methods">
              {me.externalLogins.length > 0 ? me.externalLogins.join(", ") : ta("methodsNone")}
            </dd>
          </div>
        </dl>
      </header>
      {content}
    </div>
  );
}
