import { NETWORK_PROBLEM, type ApiProblem } from "@/lib/api/problem";
import { problemFromBody } from "@/lib/client/bff";
import { createSseParser } from "@/lib/sse/parser";
import { getReadiness, withColdStartRetry } from "@/lib/readiness/controller";
import { parseAskEvent, type AskEvent } from "./events";

export interface AskRequestBody {
  question: string;
  /** Omitted: all Ready documents. */
  documentIds?: string[];
}

export type AskStreamEnd =
  /** The stream delivered an `answer` or `error` event (already passed to `onEvent`). */
  | { kind: "finished" }
  /** The request was refused before a stream started (4xx/5xx with Problem Details). */
  | { kind: "refused"; problem: ApiProblem }
  /** The connection ended with neither an answer nor an error. */
  | { kind: "dropped" }
  | { kind: "aborted" };

/**
 * Ask through our BFF relay (`POST /api/ask/stream`, never the API directly). Events are handed over as they arrive;
 * aborting the signal closes the request, which the relay forwards so the API stops working (UI-QA-7).
 */
export function askStream(
  body: AskRequestBody,
  onEvent: (event: AskEvent) => void,
  signal: AbortSignal,
): Promise<AskStreamEnd> {
  // Refused because the API is cold-starting (UI-GEN-7): nothing was sent on, so wait for it and ask once more.
  return withColdStartRetry(
    getReadiness(),
    () => askStreamOnce(body, onEvent, signal),
    (end) => end.kind === "refused" && end.problem.code === "api_starting",
  ).then((end) => (signal.aborted ? { kind: "aborted" } : end));
}

async function askStreamOnce(
  body: AskRequestBody,
  onEvent: (event: AskEvent) => void,
  signal: AbortSignal,
): Promise<AskStreamEnd> {
  let res: Response;
  try {
    res = await fetch("/api/ask/stream", {
      method: "POST",
      credentials: "same-origin",
      cache: "no-store",
      headers: { "content-type": "application/json", accept: "text/event-stream" },
      body: JSON.stringify(body),
      signal,
    });
  } catch {
    return signal.aborted ? { kind: "aborted" } : { kind: "refused", problem: NETWORK_PROBLEM };
  }

  if (!res.ok || !res.body) {
    const text = await res.text().catch(() => "");
    return {
      kind: "refused",
      problem: problemFromBody(res.status, text, res.headers.get("retry-after")),
    };
  }

  let finished = false;
  const parser = createSseParser((message) => {
    const event = parseAskEvent(message.event, message.data);
    if (!event || finished) return;
    if (event.type === "answer" || event.type === "error") finished = true;
    onEvent(event);
  });

  const reader = res.body.getReader();
  const decoder = new TextDecoder();
  try {
    for (;;) {
      const { done, value } = await reader.read();
      if (done) break;
      parser.push(decoder.decode(value, { stream: true }));
    }
    parser.push(decoder.decode());
    parser.end();
  } catch {
    return signal.aborted
      ? { kind: "aborted" }
      : finished
        ? { kind: "finished" }
        : { kind: "dropped" };
  }
  return finished
    ? { kind: "finished" }
    : signal.aborted
      ? { kind: "aborted" }
      : { kind: "dropped" };
}
