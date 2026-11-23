"use client";

import { useId, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { FormError } from "@/components/auth/form-error";
import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { deleteAccountAction } from "@/lib/auth/actions";
import type { ActionResult } from "@/lib/auth/actions-types";
import { actionWithColdStartRetry } from "@/lib/readiness/controller";

/** True when the typed text is the account's email (case and surrounding spaces do not matter). */
export const emailMatches = (typed: string, email: string): boolean =>
  typed.trim().toLowerCase() === email.trim().toLowerCase();

/**
 * Delete account (UI-ACC-3, AUTH-8): a dialog that lists what is removed, asks for the email to be typed, then deletes through a server action
 * (which also clears the session) and goes to the goodbye page with a full navigation, so no stale account state stays in the browser.
 */
export function DeleteAccount({
  email,
  action = deleteAccountAction,
  onDone = (url: string) => window.location.assign(url),
}: {
  email: string;
  /** Test seams. */
  action?: (input: { locale: string }) => Promise<ActionResult>;
  onDone?: (url: string) => void;
}) {
  const t = useTranslations("Account");
  const locale = useLocale();
  const id = useId();
  const [open, setOpen] = useState(false);
  const [typed, setTyped] = useState("");
  const [pending, setPending] = useState(false);
  const [error, setError] = useState<{ key: string; retryAfter?: number } | null>(null);

  const close = () => {
    setOpen(false);
    setTyped("");
    setError(null);
  };

  const confirm = async () => {
    if (!emailMatches(typed, email)) return;
    setPending(true);
    setError(null);
    try {
      const result = await actionWithColdStartRetry(() => action({ locale }));
      if (result.ok) return onDone(result.next ?? `/${locale}/goodbye`);
      setError({
        key: result.formError ?? "server",
        ...(result.retryAfter ? { retryAfter: result.retryAfter } : {}),
      });
    } catch {
      setError({ key: "network" });
    } finally {
      setPending(false);
    }
  };

  return (
    <section
      aria-labelledby={`${id}-title`}
      className="space-y-3 rounded-lg border border-destructive/50 p-4"
    >
      <h2 id={`${id}-title`} className="text-lg font-semibold">
        {t("delete.title")}
      </h2>
      <p className="text-sm text-muted-foreground">{t("delete.summary")}</p>
      <Button
        type="button"
        variant="outline"
        onClick={() => setOpen(true)}
        data-testid="delete-account"
      >
        {t("delete.action")}
      </Button>
      <ConfirmDialog
        open={open}
        title={t("delete.dialogTitle")}
        confirmLabel={pending ? t("delete.deleting") : t("delete.confirm")}
        cancelLabel={t("delete.cancel")}
        pending={pending}
        confirmDisabled={!emailMatches(typed, email)}
        onConfirm={() => void confirm()}
        onCancel={close}
        error={error ? <FormError messageKey={error.key} retryAfter={error.retryAfter} /> : null}
      >
        <p>{t("delete.lists")}</p>
        <ul className="list-disc space-y-0.5 ps-5">
          <li>{t("delete.listDocuments")}</li>
          <li>{t("delete.listChunks")}</li>
          <li>{t("delete.listKey")}</li>
          <li>{t("delete.listDemo")}</li>
        </ul>
        <p className="font-medium text-foreground">{t("delete.permanent")}</p>
        <div className="space-y-1.5 pt-2">
          <Label htmlFor={`${id}-email`}>
            {t.rich("delete.typeEmail", {
              email: () => (
                <bdi dir="ltr" className="font-medium text-foreground">
                  {email}
                </bdi>
              ),
            })}
          </Label>
          <Input
            id={`${id}-email`}
            dir="ltr"
            autoComplete="off"
            spellCheck={false}
            value={typed}
            onChange={(e) => setTyped(e.target.value)}
            data-testid="delete-email"
          />
        </div>
      </ConfirmDialog>
    </section>
  );
}
