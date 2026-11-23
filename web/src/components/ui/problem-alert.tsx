"use client";

import { useEffect, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { Button } from "@/components/ui/button";
import { useResetTime } from "@/components/demo/use-reset-time";
import { dynamicT } from "@/i18n/dynamic";
import { Link } from "@/i18n/navigation";
import type { ApiProblem } from "@/lib/api/problem";
import { mapProblem, type ProblemContext } from "@/lib/errors/problem-message";
import { formatBytes } from "@/lib/documents/format";

/** Seconds left of a retry wait; 0 once it is over (UI "Retry button with countdown", SRS 10.6). */
export function useCountdown(seconds: number | undefined): number {
  const [tick, setTick] = useState({ seconds, elapsed: 0 });
  // A new wait restarts the count (state derived from props during render, no effect needed).
  if (tick.seconds !== seconds) setTick({ seconds, elapsed: 0 });
  useEffect(() => {
    if (!seconds) return;
    const timer = setInterval(() => setTick((t) => ({ ...t, elapsed: t.elapsed + 1 })), 1000);
    return () => clearInterval(timer);
  }, [seconds]);
  return Math.max(0, (seconds ?? 0) - tick.elapsed);
}

/**
 * Text for a problem in the active language (SRS 10.6). Returns the message and the follow-up the table asks for.
 * `resetsAt` (the demo allowance reset, UTC) is shown as the visitor's local clock time.
 */
export function useProblemText(resetsAt?: string) {
  const t = dynamicT(useTranslations("Errors"));
  const locale = useLocale();
  const resetTime = useResetTime(resetsAt);
  return (problem: ApiProblem, ctx: ProblemContext = {}) => {
    const mapped = mapProblem(problem, ctx);
    const values: Record<string, string | number> = { ...mapped.values };
    if (typeof values.size === "number") values.size = formatBytes(values.size, locale);
    let key = mapped.key;
    if (mapped.needsTime) {
      if (resetTime) values.time = resetTime;
      else key = "demoQuotaNoTime";
    }
    return { text: t(key, values), action: mapped.action, retryAfter: problem.retryAfter };
  };
}

interface Props {
  problem: ApiProblem;
  ctx?: ProblemContext;
  resetsAt?: string | undefined;
  onRetry?: (() => void) | undefined;
  /** Called for the "refresh" action (a document is gone, a state changed). */
  onRefresh?: (() => void) | undefined;
  className?: string;
}

/** An error from the API or the BFF as a role=alert box with the next step from SRS 10.6. */
export function ProblemAlert({
  problem,
  ctx,
  resetsAt,
  onRetry,
  onRefresh,
  className = "",
}: Props) {
  const describe = useProblemText(resetsAt);
  const t = useTranslations("Errors.actions");
  const { text, action, retryAfter } = describe(problem, ctx);
  const wait = useCountdown(action === "retry" ? retryAfter : undefined);
  return (
    <div
      role="alert"
      data-testid="problem-alert"
      data-code={problem.code ?? String(problem.status)}
      className={`space-y-2 rounded-md border border-destructive/50 bg-destructive/10 p-3 text-sm ${className}`}
    >
      <p>{text}</p>
      {action === "settings" ? (
        <Link href="/settings" className="inline-block text-primary underline underline-offset-4">
          {t("settings")}
        </Link>
      ) : null}
      {action === "signIn" ? (
        <Link href="/sign-in" className="inline-block text-primary underline underline-offset-4">
          {t("signIn")}
        </Link>
      ) : null}
      {action === "resend" ? (
        <Link href="/documents" className="inline-block text-primary underline underline-offset-4">
          {t("resend")}
        </Link>
      ) : null}
      {action === "refresh" && onRefresh ? (
        <Button type="button" variant="outline" size="sm" onClick={onRefresh}>
          {t("refresh")}
        </Button>
      ) : null}
      {action === "retry" && onRetry ? (
        <Button type="button" variant="outline" size="sm" onClick={onRetry} disabled={wait > 0}>
          {wait > 0 ? t("retryIn", { seconds: wait }) : t("retry")}
        </Button>
      ) : null}
    </div>
  );
}
