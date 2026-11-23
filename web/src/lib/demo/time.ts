/** Milliseconds until `iso`, never negative; NaN input counts as zero. */
export function msUntil(iso: string, now: number): number {
  const t = Date.parse(iso);
  return Number.isFinite(t) ? Math.max(0, t - now) : 0;
}

/** Whole hours and minutes (rounded up to the minute) for a countdown label. */
export function hoursMinutes(ms: number): { hours: number; minutes: number } {
  const totalMinutes = Math.ceil(ms / 60_000);
  return { hours: Math.floor(totalMinutes / 60), minutes: totalMinutes % 60 };
}

/** Clock time of `iso` in `locale`, Western digits (OI-14). `timeZone` undefined = the runtime's own (the visitor's, in the browser). */
export function formatResetTime(iso: string, locale: string, timeZone?: string): string {
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return "";
  return new Intl.DateTimeFormat(locale, {
    timeStyle: "short",
    numberingSystem: "latn",
    ...(timeZone ? { timeZone } : {}),
  }).format(date);
}
