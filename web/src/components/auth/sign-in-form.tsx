"use client";

import { useLocale, useTranslations } from "next-intl";
import { Button } from "@/components/ui/button";
import { Link } from "@/i18n/navigation";
import { signInAction } from "@/lib/auth/actions";
import { signInSchema, type SignInInput } from "@/lib/auth/schemas";
import { FormError } from "./form-error";
import { FormField } from "./form-field";
import { useActionForm } from "./use-action-form";

export function SignInForm({ next }: { next?: string | undefined }) {
  const t = useTranslations("Auth");
  const locale = useLocale();
  const { form, formError, retryAfter, pending, onSubmit } = useActionForm<SignInInput>({
    schema: signInSchema,
    defaultValues: { email: "", password: "" },
    action: (values) => signInAction({ ...values, locale, next }),
    onSuccess: (result) => window.location.assign(result.next ?? `/${locale}/documents`),
  });
  const { register, formState } = form;

  return (
    <form onSubmit={onSubmit} noValidate className="space-y-4">
      <FormError messageKey={formError} retryAfter={retryAfter} />
      <FormField
        label={t("fields.email")}
        type="email"
        autoComplete="username"
        inputMode="email"
        dir="ltr"
        error={formState.errors.email}
        {...register("email")}
      />
      <FormField
        label={t("fields.password")}
        type="password"
        autoComplete="current-password"
        dir="ltr"
        error={formState.errors.password}
        {...register("password")}
      />
      <Button type="submit" className="w-full" disabled={pending} aria-busy={pending}>
        {pending ? t("working") : t("signIn.submit")}
      </Button>
      <p className="text-sm">
        <Link href="/forgot-password" className="text-primary underline underline-offset-4">
          {t("signIn.forgot")}
        </Link>
      </p>
    </form>
  );
}
