"use client";

import { useEffect, useSyncExternalStore } from "react";
import {
  getReadiness,
  type ReadinessController,
  type ReadinessSnapshot,
} from "@/lib/readiness/controller";

const SERVER_SNAPSHOT: ReadinessSnapshot = { phase: "idle", elapsed: 0 };

/** Current wait state of the (possibly cold-starting) API (UI-GEN-7). */
export function useApiReadiness(controller: ReadinessController = getReadiness()) {
  const snapshot = useSyncExternalStore(
    controller.subscribe,
    controller.getSnapshot,
    () => SERVER_SNAPSHOT,
  );
  return { ...snapshot, retry: () => controller.retry() };
}

/** Asks `/api/status` once on mount; the notice shows only when the API is not ready. */
export function useApiPrewarm(controller: ReadinessController = getReadiness()): void {
  useEffect(() => controller.prewarm(), [controller]);
}
