"use client";

import { useEffect, useRef } from "react";
import { useTranslations } from "next-intl";
import { dynamicT } from "@/i18n/dynamic";
import { Link } from "@/i18n/navigation";

/**
 * Error summary at the top of a failed form (UI-A11Y-3): the form-level message and a list of the fields with problems, each a link
 * to its field. It is an alert (screen readers announce it) and takes focus whenever `focusSignal` changes, so a keyboard user lands
 * on it and can follow the links. Without `fields` it is the plain form-level error.
 */
export function FormError({
  messageKey,
  retryAfter,
  fields = [],
  labels = {},
  focusSignal = 0,
}: {
  messageKey: string | null;
  retryAfter?: number | undefined;
  fields?: { name: string; keys: string[] }[];
  /** Field name to visible label. */
  labels?: Record<string, string>;
  focusSignal?: number;
}) {
  const t = useTranslations("Auth.errors");
  const tk = dynamicT(t);
  const ref = useRef<HTMLDivElement>(null);
  const visible = Boolean(messageKey) || fields.length > 0;

  useEffect(() => {
    if (focusSignal > 0 && visible) ref.current?.focus();
  }, [focusSignal, visible]);

  if (!visible) return null;
  return (
    <div
      ref={ref}
      tabIndex={-1}
      role="alert"
      data-testid="form-error"
      className="rounded-md border border-destructive/50 bg-destructive/10 p-3 text-sm text-foreground outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50"
    >
      {messageKey ? <p>{tk(messageKey, { seconds: retryAfter ?? 0 })}</p> : null}
      {fields.length > 0 ? (
        <div className={messageKey ? "mt-2" : ""}>
          <p className="font-medium">{t("summaryTitle")}</p>
          <ul className="mt-1 list-disc space-y-0.5 ps-5">
            {fields.map((f) => (
              <li key={f.name}>
                <a href={`#field-${f.name}`} className="text-primary underline underline-offset-4">
                  {labels[f.name] ? `${labels[f.name]}: ` : ""}
                  {f.keys[0] ? tk(f.keys[0]) : ""}
                </a>
              </li>
            ))}
          </ul>
        </div>
      ) : null}
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
