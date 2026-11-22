import type { Metadata } from "next";
import { hasLocale } from "next-intl";
import { getTranslations } from "next-intl/server";
import { notFound, redirect } from "next/navigation";
import { AuthCard } from "@/components/auth/auth-card";
import { ExternalButtons } from "@/components/auth/external-buttons";
import { RegisterForm } from "@/components/auth/register-form";
import { Link } from "@/i18n/navigation";
import { routing } from "@/i18n/routing";
import { getCurrentUser } from "@/lib/auth/current-user";

export async function generateMetadata({
  params,
}: PageProps<"/[locale]/register">): Promise<Metadata> {
  const { locale } = await params;
  if (!hasLocale(routing.locales, locale)) return {};
  const t = await getTranslations({ locale, namespace: "Auth.register" });
  return { title: t("title") };
}

export default async function RegisterPage({ params }: PageProps<"/[locale]/register">) {
  const { locale } = await params;
  if (!hasLocale(routing.locales, locale)) notFound();
  if ((await getCurrentUser()).status === "ok") redirect(`/${locale}/documents`);

  const t = await getTranslations("Auth");
  return (
    <AuthCard title={t("register.title")} description={t("register.description")}>
      <RegisterForm />
      <ExternalButtons locale={locale} />
      <p className="text-sm">
        {t("register.haveAccount")}{" "}
        <Link href="/sign-in" className="text-primary underline underline-offset-4">
          {t("signIn.title")}
        </Link>
      </p>
    </AuthCard>
  );
}
