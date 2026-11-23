import type { Metadata } from "next";
import { headers } from "next/headers";
import { hasLocale } from "next-intl";
import { getTranslations } from "next-intl/server";
import { notFound } from "next/navigation";
import { DeleteAccount } from "@/components/account/delete-account";
import { ResendForm } from "@/components/auth/resend-form";
import { UsageMeter } from "@/components/ui/usage-meter";
import { routing } from "@/i18n/routing";
import { getSavedKey } from "@/lib/api/llm-key";
import { requireUser } from "@/lib/auth/current-user";
import { readSession } from "@/lib/session/server";
import { Link } from "@/i18n/navigation";

export async function generateMetadata({
  params,
}: PageProps<"/[locale]/account">): Promise<Metadata> {
  const { locale } = await params;
  if (!hasLocale(routing.locales, locale)) return {};
  const t = await getTranslations({ locale, namespace: "Nav" });
  return { title: t("account") };
}

/** Account (UI-ACC-1 to 3): email and its state, linked sign-in methods, mode, storage usage and account deletion. */
export default async function AccountPage({ params }: PageProps<"/[locale]/account">) {
  const { locale } = await params;
  if (!hasLocale(routing.locales, locale)) notFound();
  const me = await requireUser(locale, "/account");
  const t = await getTranslations("Account");
  const session = await readSession();
  const key =
    me.hasLlmKey && session
      ? await getSavedKey({ visitorHeaders: await headers() }, session.at)
      : null;
  const saved = key && key.ok ? key.data : null;
  const pct = me.chunks.limit > 0 ? me.chunks.used / me.chunks.limit : 0;

  return (
    <div className="max-w-2xl space-y-8">
      <h1 className="text-3xl font-semibold tracking-tight">{t("heading")}</h1>

      <dl className="grid gap-x-6 gap-y-3 sm:grid-cols-[auto_1fr]" data-testid="account-details">
        <dt className="text-muted-foreground">{t("email")}</dt>
        <dd dir="ltr" className="text-start font-medium" data-testid="account-email">
          {me.email}
        </dd>
        <dt className="text-muted-foreground">{t("confirmation")}</dt>
        <dd data-testid="account-confirmed">
          {me.emailConfirmed ? t("confirmed") : t("notConfirmed")}
        </dd>
        <dt className="text-muted-foreground">{t("methods")}</dt>
        <dd data-testid="account-methods">
          {me.externalLogins.length > 0 ? me.externalLogins.join(", ") : t("methodsNone")}
        </dd>
        <dt className="text-muted-foreground">{t("mode")}</dt>
        <dd data-testid="account-mode">
          {me.hasLlmKey ? (
            <span>
              {t("modeKey")}
              {saved ? (
                <>
                  {" "}
                  <bdi dir="ltr">
                    ({saved.provider} / {saved.model})
                  </bdi>
                </>
              ) : null}
            </span>
          ) : me.demo ? (
            t("modeDemo")
          ) : (
            t("modeNone")
          )}{" "}
          <Link href="/settings" className="text-primary underline underline-offset-4">
            {t("manageKey")}
          </Link>
        </dd>
      </dl>

      {!me.emailConfirmed ? (
        <div className="space-y-3" data-testid="unconfirmed">
          <p className="text-sm">{t("unconfirmedHint")}</p>
          <ResendForm knownEmail />
        </div>
      ) : null}

      {me.chunks.limit > 0 ? (
        <section aria-labelledby="storage-title" className="space-y-2" data-testid="storage">
          <h2 id="storage-title" className="text-lg font-semibold">
            {t("storage.title")}
          </h2>
          <UsageMeter
            testId="meter-chunks"
            label={t("storage.chunks", { used: me.chunks.used, limit: me.chunks.limit })}
            value={me.chunks.used}
            max={me.chunks.limit}
          />
          {pct > 0.8 ? (
            <p className="text-sm" data-testid="chunks-high">
              {t("storage.high")}
            </p>
          ) : null}
          <p className="text-sm text-muted-foreground">{t("storage.help")}</p>
        </section>
      ) : null}

      <DeleteAccount email={me.email ?? ""} />
    </div>
  );
}
