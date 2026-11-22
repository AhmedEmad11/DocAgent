import type { Metadata } from "next";
import { hasLocale } from "next-intl";
import { getTranslations } from "next-intl/server";
import { notFound } from "next/navigation";
import { AuthCard, Notice } from "@/components/auth/auth-card";
import { ConfirmButton } from "@/components/auth/confirm-button";
import { Link } from "@/i18n/navigation";
import { routing } from "@/i18n/routing";

export async function generateMetadata({
  params,
}: PageProps<"/[locale]/confirm">): Promise<Metadata> {
  const { locale } = await params;
  if (!hasLocale(routing.locales, locale)) return {};
  const t = await getTranslations({ locale, namespace: "Auth.confirm" });
  return { title: t("title") };
}

const first = (v: string | string[] | undefined) => (Array.isArray(v) ? v[0] : v);

/** Target of the emailed link (plan A5): shows a button; the confirmation itself is a POST, so opening the link changes nothing. */
export default async function ConfirmPage({
  params,
  searchParams,
}: PageProps<"/[locale]/confirm">) {
  const { locale } = await params;
  if (!hasLocale(routing.locales, locale)) notFound();
  const query = await searchParams;
  const userId = first(query.userId);
  const code = first(query.code);
  const t = await getTranslations("Auth");

  return (
    <AuthCard
      title={t("confirm.title")}
      description={userId && code ? t("confirm.description") : undefined}
    >
      {userId && code ? (
        <ConfirmButton userId={userId} code={code} />
      ) : (
        <>
          <Notice tone="error">{t("errors.confirmInvalid")}</Notice>
          <Link href="/sign-in" className="text-primary underline underline-offset-4">
            {t("checkEmail.toSignIn")}
          </Link>
        </>
      )}
    </AuthCard>
  );
}
