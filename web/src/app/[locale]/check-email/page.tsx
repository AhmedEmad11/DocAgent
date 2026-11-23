import type { Metadata } from "next";
import { cookies } from "next/headers";
import { hasLocale } from "next-intl";
import { getTranslations } from "next-intl/server";
import { notFound } from "next/navigation";
import { AuthCard } from "@/components/auth/auth-card";
import { ResendForm } from "@/components/auth/resend-form";
import { Link } from "@/i18n/navigation";
import { routing } from "@/i18n/routing";
import { getServerEnv } from "@/lib/env";
import { pendingEmailCookieName } from "@/lib/session/cookie";
import { unseal } from "@/lib/session/seal";
import { sessionSecrets } from "@/lib/session/server";

export async function generateMetadata({
  params,
}: PageProps<"/[locale]/check-email">): Promise<Metadata> {
  const { locale } = await params;
  if (!hasLocale(routing.locales, locale)) return {};
  const t = await getTranslations({ locale, namespace: "Auth.checkEmail" });
  return { title: t("title") };
}

export default async function CheckEmailPage({ params }: PageProps<"/[locale]/check-email">) {
  const { locale } = await params;
  if (!hasLocale(routing.locales, locale)) notFound();
  const t = await getTranslations("Auth");

  // The address typed at registration, remembered for an hour in a sealed httpOnly cookie (never in the URL).
  const raw = (await cookies()).get(
    pendingEmailCookieName(getServerEnv().NEXT_PUBLIC_SITE_URL),
  )?.value;
  const payload = raw ? await unseal(raw, "pending-email", sessionSecrets()) : null;
  const email = typeof payload?.email === "string" ? payload.email : null;

  return (
    <AuthCard title={t("checkEmail.title")}>
      <p>
        {email ? (
          <>
            {t.rich("checkEmail.sentTo", {
              email,
              strong: (chunks) => (
                <bdi dir="ltr" className="font-semibold">
                  {chunks}
                </bdi>
              ),
            })}
          </>
        ) : (
          t("checkEmail.sent")
        )}
      </p>
      <p className="text-sm text-muted-foreground">{t("checkEmail.until")}</p>
      <p className="text-sm text-muted-foreground">{t("checkEmail.spam")}</p>
      <ResendForm knownEmail={email !== null} />
      <p className="text-sm">
        <Link href="/sign-in" className="text-primary underline underline-offset-4">
          {t("checkEmail.toSignIn")}
        </Link>
      </p>
    </AuthCard>
  );
}
