import type { Metadata } from "next";
import { hasLocale } from "next-intl";
import { getTranslations } from "next-intl/server";
import { notFound } from "next/navigation";
import { Notice } from "@/components/auth/auth-card";
import { ResendForm } from "@/components/auth/resend-form";
import { routing } from "@/i18n/routing";
import { requireUser } from "@/lib/auth/current-user";

export async function generateMetadata({
  params,
}: PageProps<"/[locale]/documents">): Promise<Metadata> {
  const { locale } = await params;
  if (!hasLocale(routing.locales, locale)) return {};
  const t = await getTranslations({ locale, namespace: "Nav" });
  return { title: t("documents") };
}

/** Signed-in landing page. The documents list itself arrives in 9.2; this page already shows the account state (UI-AUTH-2, 10). */
export default async function DocumentsPage({ params }: PageProps<"/[locale]/documents">) {
  const { locale } = await params;
  if (!hasLocale(routing.locales, locale)) notFound();
  const me = await requireUser(locale, "/documents");
  const t = await getTranslations("Account");

  return (
    <div className="max-w-2xl space-y-6">
      <h1 className="text-3xl font-semibold tracking-tight">{t("title")}</h1>
      <dl className="space-y-3">
        <div>
          <dt className="text-sm text-muted-foreground">{t("email")}</dt>
          <dd dir="ltr" className="text-start font-medium" data-testid="account-email">
            {me.email}
          </dd>
        </div>
        <div>
          <dt className="text-sm text-muted-foreground">{t("methods")}</dt>
          <dd data-testid="account-methods">
            {me.externalLogins.length > 0 ? me.externalLogins.join(", ") : t("methodsNone")}
          </dd>
        </div>
      </dl>
      {me.emailConfirmed ? (
        <p className="text-muted-foreground">{t("soon")}</p>
      ) : (
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
      )}
    </div>
  );
}
