"use client";

import { useLocale, useTranslations } from "next-intl";
import { Button } from "@/components/ui/button";
import { registerAction } from "@/lib/auth/actions";
import { registerSchema, type RegisterInput } from "@/lib/auth/schemas";
import { FormError } from "./form-error";
import { FormField } from "./form-field";
import { PasswordRules } from "./password-rules";
import { useActionForm } from "./use-action-form";

export function RegisterForm() {
  const t = useTranslations("Auth");
  const locale = useLocale();
  const { form, formError, retryAfter, pending, onSubmit } = useActionForm<RegisterInput>({
    schema: registerSchema,
    defaultValues: { email: "", password: "" },
    action: (values) => registerAction({ ...values, locale }),
    onSuccess: (result) => window.location.assign(result.next ?? `/${locale}/check-email`),
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
        label={t("fields.password")}
        type="password"
        autoComplete="new-password"
        dir="ltr"
        describedBy="password-rules"
        error={formState.errors.password}
        {...register("password")}
      />
      <PasswordRules id="password-rules" value={watch("password") ?? ""} />
      <Button type="submit" className="w-full" disabled={pending} aria-busy={pending}>
        {pending ? t("working") : t("register.submit")}
      </Button>
    </form>
  );
}
