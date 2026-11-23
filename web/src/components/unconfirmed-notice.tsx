import { getTranslations } from "next-intl/server";
import { Notice } from "@/components/auth/auth-card";
import { ResendForm } from "@/components/auth/resend-form";

/** Documents, questions, summaries and comparisons are locked until the email is confirmed (UI-AUTH-2): say why and how to fix it. */
export async function UnconfirmedNotice({ checkAgainHref }: { checkAgainHref?: string }) {
  const t = await getTranslations("Documents");
  return (
    <div className="space-y-3" data-testid="unconfirmed">
      <Notice tone="error">
        <p className="font-medium">{t("unconfirmedTitle")}</p>
        <p>{t("unconfirmedBody")}</p>
      </Notice>
      <ResendForm knownEmail />
      {checkAgainHref ? (
        <p className="text-sm">
          <a href={checkAgainHref} className="text-primary underline underline-offset-4">
            {t("checkAgain")}
          </a>
        </p>
      ) : null}
    </div>
  );
}
