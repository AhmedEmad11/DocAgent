"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/button";
import { Link } from "@/i18n/navigation";
import { confirmEmailAction } from "@/lib/auth/actions";
import { FormError } from "./form-error";

/** Confirming is a POST (server action), not a GET: link scanners that open emailed links cannot use up the code (UI-SEC-3). */
export function ConfirmButton({ userId, code }: { userId: string; code: string }) {
  const t = useTranslations("Auth");
  const [state, setState] = useState<"idle" | "pending" | "done">("idle");
  const [error, setError] = useState<string | null>(null);
  const [retryAfter, setRetryAfter] = useState<number | undefined>();

  async function confirm() {
    setState("pending");
    setError(null);
    try {
      const result = await confirmEmailAction({ userId, code });
      if (result.ok) {
        setState("done");
        return;
      }
      setError(result.formError ?? "confirmInvalid");
      setRetryAfter(result.retryAfter);
    } catch {
      setError("network");
    }
    setState("idle");
  }

  if (state === "done") {
    return (
      <div className="space-y-4">
        <p role="status" className="rounded-md border border-border bg-muted p-3 text-sm">
          {t("confirm.success")}
        </p>
        <Button asChild>
          <Link href="/documents">{t("confirm.continue")}</Link>
        </Button>
      </div>
    );
  }
  return (
    <div className="space-y-4">
      <FormError messageKey={error} retryAfter={retryAfter} />
      <Button onClick={confirm} disabled={state === "pending"} aria-busy={state === "pending"}>
        {state === "pending" ? t("working") : t("confirm.button")}
      </Button>
    </div>
  );
}
