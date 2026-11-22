import type { Metadata } from "next";
import { hasLocale } from "next-intl";
import { getTranslations } from "next-intl/server";
import { notFound, redirect } from "next/navigation";
import { AuthCard, Notice } from "@/components/auth/auth-card";
import { ExternalButtons } from "@/components/auth/external-buttons";
import { SignInForm } from "@/components/auth/sign-in-form";
import { Link } from "@/i18n/navigation";
import { routing } from "@/i18n/routing";
import { toExternalError } from "@/lib/auth/error-map";
import { getCurrentUser } from "@/lib/auth/current-user";
import { safeNextPath } from "@/lib/auth/next-path";

export async function generateMetadata({
  params,
}: PageProps<"/[locale]/sign-in">): Promise<Metadata> {
  const { locale } = await params;
  if (!hasLocale(routing.locales, locale)) return {};
  const t = await getTranslations({ locale, namespace: "Auth.signIn" });
  return { title: t("title") };
}

const first = (v: string | string[] | undefined) => (Array.isArray(v) ? v[0] : v);

export default async function SignInPage({ params, searchParams }: PageProps<"/[locale]/sign-in">) {
  const { locale } = await params;
  if (!hasLocale(routing.locales, locale)) notFound();
  const query = await searchParams;
  const next = safeNextPath(first(query.next), locale);

  // Signed-in users go straight into the app (UI-AUTH-9).
  if ((await getCurrentUser()).status === "ok") redirect(next);

  const t = await getTranslations("Auth");
  const error = first(query.error);
  return (
    <AuthCard title={t("signIn.title")}>
      {first(query.reset) === "1" ? <Notice>{t("signIn.resetDone")}</Notice> : null}
      {first(query.reason) === "session" ? <Notice>{t("signIn.sessionEnded")}</Notice> : null}
      {error ? (
        <Notice tone="error">{t(`signIn.externalErrors.${toExternalError(error)}`)}</Notice>
      ) : null}
      <SignInForm next={next} />
      <ExternalButtons locale={locale} next={next} />
      <p className="text-sm">
        {t("signIn.noAccount")}{" "}
        <Link href="/register" className="text-primary underline underline-offset-4">
          {t("register.nav")}
        </Link>
      </p>
    </AuthCard>
  );
}
