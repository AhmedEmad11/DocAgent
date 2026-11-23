"use client";

import { useState } from "react";
import { useForm, type FieldValues, type Path, type UseFormReturn } from "react-hook-form";
import type { z } from "zod";
import type { ActionResult } from "@/lib/auth/actions-types";
import { actionWithColdStartRetry } from "@/lib/readiness/controller";
import { zodResolver } from "@/lib/forms/zod-resolver";
import { errorKeys } from "./form-field";

interface Options<T extends FieldValues> {
  schema: z.ZodType<T, FieldValues>;
  defaultValues: T;
  /** Calls the server action. */
  action: (values: T) => Promise<ActionResult>;
  /** Success handler: usually a hard navigation, so the new cookie is picked up by every server component. */
  onSuccess: (result: Extract<ActionResult, { ok: true }>, values: T) => void;
}

/** What the error summary at the top of a failed form needs (UI-A11Y-3). */
export interface ErrorSummaryProps {
  messageKey: string | null;
  retryAfter: number | undefined;
  /** Fields with errors, in form order; `keys` are message keys under `Auth.errors`. */
  fields: { name: string; keys: string[] }[];
  /** Goes up on every failed submit; the summary takes focus when it changes. */
  focusSignal: number;
}

export interface ActionForm<T extends FieldValues> {
  form: UseFormReturn<T>;
  formError: string | null;
  retryAfter: number | undefined;
  summary: ErrorSummaryProps;
  pending: boolean;
  onSubmit: (e?: React.BaseSyntheticEvent) => Promise<void>;
}

/** React Hook Form + zod on the client, the same schema again in the server action. Errors are message keys. */
export function useActionForm<T extends FieldValues>(opts: Options<T>): ActionForm<T> {
  const form = useForm<T>({
    resolver: zodResolver(opts.schema),
    defaultValues: opts.defaultValues as never,
    criteriaMode: "all",
    // The summary takes focus instead of the first field (UI-A11Y-3); its links lead to the fields.
    shouldFocusError: false,
  });
  const [focusSignal, setFocusSignal] = useState(0);
  const [formError, setFormError] = useState<string | null>(null);
  const [retryAfter, setRetryAfter] = useState<number | undefined>();
  const [pending, setPending] = useState(false);

  const onSubmit = form.handleSubmit(
    async (values) => {
      setFormError(null);
      setRetryAfter(undefined);
      setPending(true);
      try {
        const result = await actionWithColdStartRetry(() => opts.action(values));
        if (result.ok) {
          opts.onSuccess(result, values);
          return;
        }
        for (const [field, keys] of Object.entries(result.fieldErrors ?? {})) {
          form.setError(field as Path<T>, {
            type: "server",
            message: keys[0] ?? "",
            types: Object.fromEntries(keys.map((k) => [k, k])),
          });
        }
        setFormError(result.formError ?? (result.fieldErrors ? null : "server"));
        setRetryAfter(result.retryAfter);
        setFocusSignal((n) => n + 1);
      } catch {
        setFormError("network");
        setFocusSignal((n) => n + 1);
      } finally {
        setPending(false);
      }
    },
    () => setFocusSignal((n) => n + 1),
  );

  const fields = Object.entries(form.formState.errors).map(([name, error]) => ({
    name,
    keys: errorKeys(error as never),
  }));

  return {
    form,
    formError,
    retryAfter,
    pending,
    onSubmit,
    summary: { messageKey: formError, retryAfter, fields, focusSignal },
  };
}
