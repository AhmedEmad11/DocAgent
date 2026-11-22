"use client";

import { useTranslations } from "next-intl";
import type { FieldError } from "react-hook-form";
import { dynamicT } from "@/i18n/dynamic";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";

export function errorKeys(error: FieldError | undefined): string[] {
  if (!error) return [];
  const keys = Object.keys(error.types ?? {});
  return keys.length > 0 ? keys : error.message ? [error.message] : [];
}

interface FormFieldProps extends Omit<React.ComponentProps<"input">, "id" | "name"> {
  name: string;
  label: string;
  error?: FieldError | undefined;
  hint?: string;
  /** Extra id(s) of elements that describe the field (for example the password rules). */
  describedBy?: string;
}

/** Label, input and per-field messages, wired with aria-invalid and aria-describedby. */
export function FormField({
  name,
  label,
  error,
  hint,
  describedBy: extra,
  ...input
}: FormFieldProps) {
  const t = dynamicT(useTranslations("Auth.errors"));
  const keys = errorKeys(error);
  const id = `field-${name}`;
  const describedBy = [extra, hint ? `${id}-hint` : null, keys.length > 0 ? `${id}-error` : null]
    .filter(Boolean)
    .join(" ");
  return (
    <div className="space-y-1.5">
      <Label htmlFor={id}>{label}</Label>
      <Input
        id={id}
        name={name}
        aria-invalid={keys.length > 0 || undefined}
        aria-describedby={describedBy || undefined}
        {...input}
      />
      {hint ? (
        <p id={`${id}-hint`} className="text-sm text-muted-foreground">
          {hint}
        </p>
      ) : null}
      {keys.length > 0 ? (
        <ul id={`${id}-error`} className="space-y-0.5 text-sm text-destructive">
          {keys.map((k) => (
            <li key={k}>{t(k)}</li>
          ))}
        </ul>
      ) : null}
    </div>
  );
}
