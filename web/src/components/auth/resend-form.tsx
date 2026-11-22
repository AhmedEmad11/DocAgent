"use client";

import { useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { z } from "zod";
import { Button } from "@/components/ui/button";
import { resendConfirmationAction } from "@/lib/auth/actions";
import { resendSchema, type ResendInput } from "@/lib/auth/schemas";
import { FormError } from "./form-error";
import { FormField } from "./form-field";
import { useActionForm } from "./use-action-form";

// With a remembered address there is nothing to validate; the server action fills the email in.
const knownSchema = z.object({ email: z.string() });

/**
 * Resend the confirmation email (UI-AUTH-2). With `knownEmail` (signed-in user, or the address remembered after registering)
 * it is a single button; otherwise it asks for the address.
 */
export function ResendForm({ knownEmail }: { knownEmail?: boolean }) {
  const t = useTranslations("Auth");
  const locale = useLocale();
  const [sent, setSent] = useState(false);
  const { form, formError, retryAfter, pending, onSubmit } = useActionForm<ResendInput>({
    schema: knownEmail ? knownSchema : resendSchema,
    defaultValues: { email: "" },
    action: (values) => resendConfirmationAction({ email: values.email || undefined, locale }),
    onSuccess: () => setSent(true),
  });

  return (
    <form onSubmit={onSubmit} noValidate className="space-y-3">
      <FormError messageKey={formError} retryAfter={retryAfter} />
      {sent ? (
        <p role="status" className="rounded-md border border-border bg-muted p-3 text-sm">
          {t("checkEmail.resent")}
        </p>
      ) : null}
      {knownEmail ? null : (
        <FormField
          label={t("fields.email")}
          type="email"
          autoComplete="email"
          inputMode="email"
          dir="ltr"
          error={form.formState.errors.email}
          {...form.register("email")}
        />
      )}
      <Button type="submit" variant="outline" disabled={pending} aria-busy={pending}>
        {pending ? t("working") : t("checkEmail.resend")}
      </Button>
    </form>
  );
}
