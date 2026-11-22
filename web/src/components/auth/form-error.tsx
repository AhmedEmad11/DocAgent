"use client";

import { useTranslations } from "next-intl";
import { dynamicT } from "@/i18n/dynamic";
import { Link } from "@/i18n/navigation";

/** Form-level error from a message key (role=alert so screen readers announce it). */
export function FormError({
  messageKey,
  retryAfter,
}: {
  messageKey: string | null;
  retryAfter?: number | undefined;
}) {
  const t = useTranslations("Auth.errors");
  const tk = dynamicT(t);
  if (!messageKey) return null;
  return (
    <div
      role="alert"
      className="rounded-md border border-destructive/50 bg-destructive/10 p-3 text-sm text-foreground"
    >
      <p>{tk(messageKey, { seconds: retryAfter ?? 0 })}</p>
      {messageKey === "registerFailed" ? (
        <p className="mt-1">
          <Link href="/sign-in" className="text-primary underline underline-offset-4">
            {t("registerFailedSignIn")}
          </Link>
        </p>
      ) : null}
      {messageKey === "lockedOut" ? (
        <p className="mt-1">
          <Link href="/forgot-password" className="text-primary underline underline-offset-4">
            {t("lockedOutReset")}
          </Link>
        </p>
      ) : null}
    </div>
  );
}
