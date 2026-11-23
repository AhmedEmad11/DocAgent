import { answerSchema, type Answer } from "@/lib/ask/events";
import { bffFetch, type BffResult } from "@/lib/client/bff";

export const MAX_FOCUS = 500;

export type WorkflowRequest =
  | { kind: "summary"; documentId: string; focus: string }
  | { kind: "compare"; documentIdA: string; documentIdB: string; focus: string };

/** Pre-submit checks the API repeats (UI-SUM-2): two different documents, focus within 500 characters. */
export function validateWorkflow(
  request: WorkflowRequest,
): "chooseDocument" | "chooseTwo" | "sameDocument" | "focusTooLong" | null {
  if (request.focus.trim().length > MAX_FOCUS) return "focusTooLong";
  if (request.kind === "summary") return request.documentId ? null : "chooseDocument";
  if (!request.documentIdA || !request.documentIdB) return "chooseTwo";
  if (request.documentIdA === request.documentIdB) return "sameDocument";
  return null;
}

/** Summary and compare are not streamed (UI-SUM-3): one call through the allowlisted proxy, abortable. */
export function runWorkflow(
  request: WorkflowRequest,
  signal: AbortSignal,
): Promise<BffResult<Answer>> {
  const focus = request.focus.trim();
  const json = focus ? { focus } : {};
  return request.kind === "summary"
    ? bffFetch(
        `/api/proxy/documents/${request.documentId}/summary`,
        { method: "POST", json, signal },
        (b) => answerSchema.parse(b),
      )
    : bffFetch(
        "/api/proxy/documents/compare",
        {
          method: "POST",
          json: { documentIdA: request.documentIdA, documentIdB: request.documentIdB, ...json },
          signal,
        },
        (b) => answerSchema.parse(b),
      );
}
