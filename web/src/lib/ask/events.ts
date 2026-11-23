import { z } from "zod";

// The API's answer (`POST /ask`, the stream's `answer` event, summary and compare): SRS 10.5.
export const citationSchema = z.object({
  index: z.number().int(),
  documentId: z.string(),
  fileName: z.string(),
  pageNumber: z.number().nullish(),
  section: z.string().nullish(),
  quote: z.string(),
});
export type Citation = z.infer<typeof citationSchema>;

export const answerSchema = z.object({
  answer: z.string(),
  notFound: z.boolean(),
  citations: z.array(citationSchema),
  mode: z.string(),
  provider: z.string(),
  model: z.string(),
  incomplete: z.boolean(),
  toolCalls: z.number().optional(),
  notices: z.array(z.string()),
});
export type Answer = z.infer<typeof answerSchema>;

const errorEventSchema = z.object({
  status: z.number(),
  title: z.string(),
  code: z.string().nullish(),
  retryAfter: z.number().nullish(),
});

export type AskEvent =
  | { type: "searching" }
  | { type: "tool_call" }
  | { type: "verifying" }
  | { type: "answer"; answer: Answer }
  | { type: "error"; status: number; title: string; code?: string; retryAfter?: number };

/** One SSE message to a typed event; null for unknown events and malformed data (ignored, never fatal). Tool names are dropped (OI-11). */
export function parseAskEvent(event: string, data: string): AskEvent | null {
  switch (event) {
    case "searching":
    case "verifying":
    case "tool_call":
      return { type: event };
    case "answer": {
      const parsed = answerSchema.safeParse(safeJson(data));
      return parsed.success ? { type: "answer", answer: parsed.data } : null;
    }
    case "error": {
      const parsed = errorEventSchema.safeParse(safeJson(data));
      if (!parsed.success) return { type: "error", status: 500, title: "Error" };
      const e = parsed.data;
      return {
        type: "error",
        status: e.status,
        title: e.title,
        ...(e.code ? { code: e.code } : {}),
        ...(e.retryAfter ? { retryAfter: e.retryAfter } : {}),
      };
    }
    default:
      return null;
  }
}

function safeJson(text: string): unknown {
  try {
    return JSON.parse(text);
  } catch {
    return undefined;
  }
}
