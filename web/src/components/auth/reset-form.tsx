"use client";

import { useLocale, useTranslations } from "next-intl";
import { Button } from "@/components/ui/button";
import { resetPasswordAction } from "@/lib/auth/actions";
import { resetSchema, type ResetInput } from "@/lib/auth/schemas";
import { FormError } from "./form-error";
import { FormField } from "./form-field";
import { PasswordRules } from "./password-rules";
import { useActionForm } from "./use-action-form";

export function ResetForm() {
  const t = useTranslations("Auth");
  const locale = useLocale();
  const { form, formError, retryAfter, pending, onSubmit } = useActionForm<ResetInput>({
    schema: resetSchema,
    defaultValues: { email: "", resetCode: "", newPassword: "" },
    action: (values) => resetPasswordAction({ ...values, locale }),
    onSuccess: (result) => window.location.assign(result.next ?? `/${locale}/sign-in`),
  });
  const { register, formState, watch } = form;

  return (
    <form onSubmit={onSubmit} noValidate className="space-y-4">
      <FormError messageKey={formError} retryAfter={retryAfter} />
      <FormField
        label={t("fields.email")}
        type="email"
        autoComplete="email"
        inputMode="email"
        dir="ltr"
        error={formState.errors.email}
        {...register("email")}
      />
      <FormField
        label={t("fields.resetCode")}
        autoComplete="one-time-code"
        dir="ltr"
        hint={t("reset.codeHint")}
        error={formState.errors.resetCode}
        {...register("resetCode")}
      />
      <FormField
        label={t("fields.newPassword")}
        type="password"
        autoComplete="new-password"
        dir="ltr"
        describedBy="password-rules"
        error={formState.errors.newPassword}
        {...register("newPassword")}
      />
      <PasswordRules id="password-rules" value={watch("newPassword") ?? ""} />
      <Button type="submit" className="w-full" disabled={pending} aria-busy={pending}>
        {pending ? t("working") : t("reset.submit")}
      </Button>
    </form>
  );
}
