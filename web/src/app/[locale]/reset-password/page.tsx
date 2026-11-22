import type { Metadata } from "next";
import { hasLocale } from "next-intl";
import { getTranslations } from "next-intl/server";
import { notFound, redirect } from "next/navigation";
import { AuthCard } from "@/components/auth/auth-card";
import { ResetForm } from "@/components/auth/reset-form";
import { routing } from "@/i18n/routing";
import { getCurrentUser } from "@/lib/auth/current-user";

export async function generateMetadata({
  params,
}: PageProps<"/[locale]/reset-password">): Promise<Metadata> {
  const { locale } = await params;
  if (!hasLocale(routing.locales, locale)) return {};
  const t = await getTranslations({ locale, namespace: "Auth.reset" });
  return { title: t("title") };
}

export default async function ResetPasswordPage({ params }: PageProps<"/[locale]/reset-password">) {
  const { locale } = await params;
  if (!hasLocale(routing.locales, locale)) notFound();
  if ((await getCurrentUser()).status === "ok") redirect(`/${locale}/documents`);
  const t = await getTranslations("Auth");
  return (
    <AuthCard title={t("reset.title")} description={t("reset.description")}>
      <ResetForm />
    </AuthCard>
  );
}
