import type { ApiDocument } from "@/lib/api/documents-schema";

// Status polling (UI-DOC-4, UI-PERF-5): every 2 to 5 s with back-off, never faster than one request per 2 s,
// paused while the tab is hidden, stopped when every document is final.
export const MIN_POLL_MS = 2000;
export const MAX_POLL_MS = 5000;

/** Delay before the next poll; `unchangedPolls` is how many polls in a row showed no status change. */
export function pollDelayMs(unchangedPolls: number): number {
  return Math.min(MAX_POLL_MS, MIN_POLL_MS + Math.max(0, unchangedPolls) * 500);
}

export const isActive = (d: Pick<ApiDocument, "status">): boolean =>
  d.status === "Pending" || d.status === "Processing";

export const hasActive = (docs: readonly Pick<ApiDocument, "status">[]): boolean =>
  docs.some(isActive);

export interface StatusChange {
  id: string;
  fileName: string;
  from: ApiDocument["status"] | null;
  to: ApiDocument["status"];
}

/** Documents whose status differs from the previous list (new documents count as changes only when they appear final). */
export function statusChanges(
  prev: readonly ApiDocument[],
  next: readonly ApiDocument[],
): StatusChange[] {
  const before = new Map(prev.map((d) => [d.id, d.status]));
  const changes: StatusChange[] = [];
  for (const d of next) {
    const was = before.get(d.id) ?? null;
    if (was !== d.status && (was !== null || !isActive(d)))
      changes.push({ id: d.id, fileName: d.fileName, from: was, to: d.status });
  }
  return changes;
}
