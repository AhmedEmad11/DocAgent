import { NextResponse } from "next/server";
import { getServerEnv } from "@/lib/env";
import { fetchImpl, triggerWake } from "./client";

const STATUS_TIMEOUT_MS = 4_000;

/**
 * `GET /api/status` (UI-GEN-7): is the API awake? Asks the API's liveness endpoint (outside `/api/v1`) with a short timeout. Not ready
 * wakes the host (deduplicated in `triggerWake`). No auth and nothing about the visitor is forwarded; answered with 200 either way.
 */
export async function handleStatus(): Promise<NextResponse> {
  let ready = false;
  try {
    const res = await fetchImpl.current(`${getServerEnv().DOCAGENT_API_URL}/health/live`, {
      method: "GET",
      headers: { accept: "application/json" },
      signal: AbortSignal.timeout(STATUS_TIMEOUT_MS),
      redirect: "manual",
      cache: "no-store",
    });
    await res.body?.cancel();
    ready = res.status === 200;
  } catch {
    ready = false;
  }
  if (!ready) triggerWake();
  return NextResponse.json({ ready }, { headers: { "cache-control": "no-store" } });
}
