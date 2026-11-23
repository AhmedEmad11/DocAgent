"use client";

import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/button";
import { useApiReadiness } from "./use-api-readiness";

/**
 * Polite status notice while the API wakes up (UI-GEN-7). The live region is always in the page so the text is announced when it
 * appears; the progress bar is indeterminate and does not move for people who prefer reduced motion. Forms stay usable.
 */
export function ApiStartingNotice() {
  const t = useTranslations("Readiness");
  const { phase, elapsed, retry } = useApiReadiness();
  return (
    <div
      role="status"
      aria-live="polite"
      data-testid="api-starting"
      className="pointer-events-none fixed inset-x-0 bottom-4 z-40 flex justify-center px-4"
    >
      {phase === "starting" ? (
        <div className="pointer-events-auto w-full max-w-md rounded-md border border-border bg-card p-4 text-sm text-card-foreground shadow-md">
          <p className="font-medium">{t("title")}</p>
          <p className="mt-1 text-muted-foreground">{t("body", { seconds: elapsed })}</p>
          <div className="mt-3 h-1.5 overflow-hidden rounded-full bg-muted" aria-hidden="true">
            <div className="h-full w-1/3 rounded-full bg-primary motion-safe:animate-[cold-start-slide_1.6s_ease-in-out_infinite] motion-reduce:w-full motion-reduce:opacity-60" />
          </div>
        </div>
      ) : null}
      {phase === "timeout" ? (
        <div className="pointer-events-auto w-full max-w-md rounded-md border border-border bg-card p-4 text-sm text-card-foreground shadow-md">
          <p>{t("timeout")}</p>
          <Button type="button" size="sm" className="mt-3" onClick={retry}>
            {t("retry")}
          </Button>
        </div>
      ) : null}
    </div>
  );
}
