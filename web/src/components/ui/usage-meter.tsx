import { useId } from "react";

/** A labelled meter (UI-ACC-2, UI-DOC-8): text says the numbers, the bar only repeats them. A native `<progress>` fills from the
 * start edge in both directions and needs no inline style (the CSP forbids style attributes, UI-SEC-4). */
export function UsageMeter({
  label,
  value,
  max,
  testId,
}: {
  label: string;
  value: number;
  max: number;
  testId?: string;
}) {
  const id = useId();
  const high = max > 0 && value / max >= 0.8;
  return (
    <div data-testid={testId} className="space-y-1.5">
      <p id={id} className="text-sm">
        {label}
      </p>
      <progress
        aria-labelledby={id}
        className="meter"
        data-high={high || undefined}
        value={Math.min(value, max)}
        max={max}
      />
    </div>
  );
}
