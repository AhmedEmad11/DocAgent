"use client";

import { useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Button } from "@/components/ui/button";
import { Link } from "@/i18n/navigation";
import { forgotPasswordAction } from "@/lib/auth/actions";
import { forgotSchema, type ForgotInput } from "@/lib/auth/schemas";
import { FormError } from "./form-error";
import { FormField } from "./form-field";
import { useActionForm } from "./use-action-form";

export function ForgotForm() {
  const t = useTranslations("Auth");
  const locale = useLocale();
  const [sent, setSent] = useState(false);
  const { form, formError, retryAfter, pending, onSubmit } = useActionForm<ForgotInput>({
    schema: forgotSchema,
    defaultValues: { email: "" },
    action: (values) => forgotPasswordAction({ ...values, locale }),
    onSuccess: () => setSent(true),
  });

  if (sent) {
    // The same message whether or not the account exists (UI-AUTH-5).
    return (
      <div className="space-y-4">
        <p role="status" className="rounded-md border border-border bg-muted p-3 text-sm">
          {t("forgot.sent")}
        </p>
        <Button asChild>
          <Link href="/reset-password">{t("forgot.toReset")}</Link>
        </Button>
      </div>
    );
  }

  return (
    <form onSubmit={onSubmit} noValidate className="space-y-4">
      <FormError messageKey={formError} retryAfter={retryAfter} />
      <FormField
        label={t("fields.email")}
        type="email"
        autoComplete="email"
        inputMode="email"
        dir="ltr"
        error={form.formState.errors.email}
        {...form.register("email")}
      />
      <Button type="submit" className="w-full" disabled={pending} aria-busy={pending}>
        {pending ? t("working") : t("forgot.submit")}
      </Button>
    </form>
  );
}
