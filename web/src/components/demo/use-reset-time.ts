"use client";

import { useSyncExternalStore } from "react";
import { useLocale } from "next-intl";
import { formatResetTime } from "@/lib/demo/time";

const never = () => () => {};

/**
 * The reset time in the visitor's own time zone (UI-I18N-5: `resetsAt` is UTC). During the server render and hydration there is no
 * visitor zone, so UTC is used until the client takes over a moment later (no hydration mismatch).
 */
export function useResetTime(iso: string | undefined): string {
  const locale = useLocale();
  return useSyncExternalStore(
    never,
    () => (iso ? formatResetTime(iso, locale) : ""),
    () => (iso ? formatResetTime(iso, locale, "UTC") : ""),
  );
}
