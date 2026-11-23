"use client";

import { useEffect, useRef, useSyncExternalStore } from "react";
import { useTranslations } from "next-intl";
import { useRouter } from "next/navigation";
import { Link } from "@/i18n/navigation";
import { isKnownPrivacyNotice } from "@/lib/demo/privacy";
import { hoursMinutes, msUntil } from "@/lib/demo/time";
import { useResetTime } from "./use-reset-time";

export interface DemoState {
  limit: number;
  used: number;
  remaining: number;
  resetsAt: string;
  privacyNotice: string;
}

const SEEN_KEY = "docagent.demoNoticeSeen";
const SEEN_AFTER_MS = 3000;

function safeSession<T>(fn: (s: Storage) => T, fallback: T): T {
  try {
    return fn(window.sessionStorage);
  } catch {
    return fallback; // storage blocked (private mode): the notice simply opens each time
  }
}

/** A clock for the countdown: ticks every 30 s, and gives the server a stable value so hydration matches. */
function useNow(active: boolean): number {
  return useSyncExternalStore(
    (notify) => {
      if (!active) return () => {};
      const timer = setInterval(notify, 30_000);
      return () => clearInterval(timer);
    },
    () => (active ? Math.floor(Date.now() / 30_000) * 30_000 : 0),
    () => 0,
  );
}

/** The API's privacy notice in the active language when it is the one this UI knows, else as given (English, left to right). */
export function PrivacyText({ text }: { text: string }) {
  const t = useTranslations("Demo");
  if (isKnownPrivacyNotice(text)) return <>{t("privacyNotice")}</>;
  return (
    <span dir="ltr" className="inline-block text-start">
      {text}
    </span>
  );
}

/**
 * The demo banner (UI-DEMO-1 to 3): the live allowance from the API (`limit`, `used`, `remaining`), the reset time in the visitor's local
 * time zone, a link to the key settings, and the privacy notice (PRIV-1) expanded the first time in a browser session and available on
 * demand afterwards. At zero questions left it counts down to the reset and refreshes the page data when the time comes.
 */
export function DemoBannerView({ demo }: { demo: DemoState }) {
  const t = useTranslations("Demo");
  const router = useRouter();
  const details = useRef<HTMLDetailsElement>(null);
  const opening = useRef(false);
  const exhausted = demo.remaining <= 0;
  const now = useNow(exhausted);
  const left = exhausted && now > 0 ? msUntil(demo.resetsAt, now) : null;

  useEffect(() => {
    // First time in this browser session: open the notice (an uncontrolled <details>, so no state and no hydration mismatch). It counts as
    // seen only after it stayed on screen for a few seconds (or the visitor toggled it): the sign-in action briefly renders this banner on
    // the sign-in page before the redirect, and that must not use up the first view.
    if (safeSession((s) => s.getItem(SEEN_KEY), "1")) return;
    if (details.current) {
      opening.current = true; // the toggle event this causes is not the visitor's
      details.current.open = true;
    }
    const timer = setTimeout(
      () => safeSession((s) => s.setItem(SEEN_KEY, "1"), undefined),
      SEEN_AFTER_MS,
    );
    return () => clearTimeout(timer);
  }, []);

  useEffect(() => {
    // The allowance resets: re-read /me through the server render.
    if (left === 0) router.refresh();
  }, [left, router]);

  const time = useResetTime(demo.resetsAt);
  const { hours, minutes } = hoursMinutes(left ?? 0);

  return (
    <div
      role="region"
      aria-label={t("region")}
      data-testid="demo-banner"
      className="border-b border-border bg-muted"
    >
      <div className="mx-auto flex max-w-5xl flex-wrap items-center gap-x-4 gap-y-2 px-4 py-2 text-sm">
        <p role="status" data-testid="demo-allowance" suppressHydrationWarning>
          {exhausted
            ? t("bannerZero", {
                limit: demo.limit,
                time,
                countdown: left === null ? "…" : t("countdown", { hours, minutes }),
              })
            : t("banner", { remaining: demo.remaining, limit: demo.limit, time })}
        </p>
        <Link href="/settings" className="text-primary underline underline-offset-4">
          {t("addKey")}
        </Link>
        <details
          ref={details}
          className="basis-full"
          data-testid="demo-privacy"
          onToggle={() => {
            if (opening.current) opening.current = false;
            else safeSession((s) => s.setItem(SEEN_KEY, "1"), undefined);
          }}
        >
          <summary className="cursor-pointer text-primary underline underline-offset-4">
            {t("privacy")}
          </summary>
          <p className="mt-1 max-w-3xl text-start" data-testid="demo-privacy-text">
            <PrivacyText text={demo.privacyNotice} />
          </p>
        </details>
      </div>
    </div>
  );
}
