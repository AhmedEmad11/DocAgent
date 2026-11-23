"use client";

import { useApiPrewarm } from "./use-api-readiness";

/** Renders nothing: put it on pages that talk to the API so a sleeping API is woken while the visitor reads or types. */
export function ApiPrewarm() {
  useApiPrewarm();
  return null;
}
