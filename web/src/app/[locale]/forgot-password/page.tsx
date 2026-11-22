import type { Metadata } from "next";
import { hasLocale } from "next-intl";
import { getTranslations } from "next-intl/server";
import { notFound, redirect } from "next/navigation";
import { AuthCard } from "@/components/auth/auth-card";
import { ForgotForm } from "@/components/auth/forgot-form";
import { Link } from "@/i18n/navigation";
import { routing } from "@/i18n/routing";
import { getCurrentUser } from "@/lib/auth/current-user";

export async function generateMetadata({
  params,
}: PageProps<"/[locale]/forgot-password">): Promise<Metadata> {
  const { locale } = await params;
  if (!hasLocale(routing.locales, locale)) return {};
  const t = await getTranslations({ locale, namespace: "Auth.forgot" });
  return { title: t("title") };
}

export default async function ForgotPasswordPage({
  params,
}: PageProps<"/[locale]/forgot-password">) {
  const { locale } = await params;
  if (!hasLocale(routing.locales, locale)) notFound();
  if ((await getCurrentUser()).status === "ok") redirect(`/${locale}/documents`);
  const t = await getTranslations("Auth");
  return (
    <AuthCard title={t("forgot.title")} description={t("forgot.description")}>
      <ForgotForm />
      <p className="text-sm">
        <Link href="/reset-password" className="text-primary underline underline-offset-4">
          {t("forgot.haveCode")}
        </Link>
      </p>
    </AuthCard>
  );
}
