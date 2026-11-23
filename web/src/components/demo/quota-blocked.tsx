"use client";

import { ProblemAlert } from "@/components/ui/problem-alert";

export interface Quota {
  remaining: number;
  resetsAt: string;
}

export const isBlocked = (quota: Quota | null | undefined): boolean =>
  Boolean(quota) && (quota?.remaining ?? 1) <= 0;

/** Shown instead of working controls when the daily demo allowance is used up (UI-DEMO-2): the reset time and the way to add a key. The API still enforces the limit. */
export function QuotaBlocked({ resetsAt }: { resetsAt: string }) {
  return (
    <ProblemAlert
      problem={{ status: 429, title: "Daily demo limit reached", code: "demo_quota_exceeded" }}
      resetsAt={resetsAt}
    />
  );
}
