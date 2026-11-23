import type { ApiProblem } from "@/lib/api/problem";

// API errors -> message keys under `Errors.*` (SRS 10.6, UI-I18N-7). Pure, so every code is unit tested in both languages.
// Where the same status means different things per endpoint (409, 429, 400/422 for keys) the `area` decides.

export type ErrorArea =
  | "upload"
  | "delete"
  | "documents"
  | "ask"
  | "summary"
  | "compare"
  | "key-save"
  | "key"
  | "account";

export type ErrorAction = "settings" | "retry" | "signIn" | "resend" | "refresh" | null;

export interface ProblemContext {
  area?: ErrorArea;
  /** Document limit of the account (409 on upload). */
  documentLimit?: number;
  /** File size limit of the account in bytes (413). */
  maxBytes?: number;
  /** Provider name for `llm_no_credit` and `llm_key_invalid` on save. */
  provider?: string;
}

export interface MappedProblem {
  /** Key under `Errors.`. */
  key: string;
  /** ICU values; `seconds` and `size`/`limit`/`provider` come from the problem or context, `time` is filled by the renderer. */
  values: Record<string, string | number>;
  action: ErrorAction;
  /** True when the message asks for a time (`{time}`, the demo reset) the renderer must supply. */
  needsTime?: boolean;
}

/** Every key this module can return; the catalogs must have all of them (unit test). */
export const ERROR_KEYS = [
  "demoQuota",
  "demoQuotaNoTime",
  "demoBusy",
  "demoUnavailable",
  "keyRequired",
  "keyInvalidSave",
  "keyInvalidUse",
  "noCredit",
  "providerLimit",
  "providerDown",
  "modelUnknown",
  "requestRejected",
  "keyUnreadable",
  "checkFields",
  "sessionEnded",
  "confirmEmail",
  "docGone",
  "documentLimit",
  "docNotReady",
  "conflict",
  "tooLarge",
  "unsupported",
  "tooMany",
  "tooManyNoWait",
  "maxQuestions",
  "server",
  "serviceDown",
  "tookTooLong",
  "network",
  "forbidden",
  "generic",
] as const;
export type ErrorKey = (typeof ERROR_KEYS)[number];

const seconds = (p: ApiProblem) => ({ seconds: p.retryAfter ?? 0 });

export function mapProblem(problem: ApiProblem, ctx: ProblemContext = {}): MappedProblem {
  const m = (
    key: ErrorKey,
    action: ErrorAction = null,
    values: Record<string, string | number> = {},
    needsTime = false,
  ): MappedProblem => ({ key, values, action, ...(needsTime ? { needsTime } : {}) });
  const provider = ctx.provider ?? "";

  switch (problem.code) {
    case "demo_quota_exceeded":
      return m("demoQuota", "settings", {}, true);
    case "demo_rate_limited":
      return m("demoBusy", "retry", seconds(problem));
    case "demo_unavailable":
      return m("demoUnavailable", "settings");
    case "llm_key_required":
      return m("keyRequired", "settings");
    case "llm_key_invalid":
      return ctx.area === "key-save"
        ? m("keyInvalidSave", null, { provider })
        : m("keyInvalidUse", "settings");
    case "llm_no_credit":
      return m("noCredit", "settings", { provider });
    case "llm_rate_limited":
      return m("providerLimit", "retry", seconds(problem));
    case "llm_unavailable":
      return m("providerDown", "retry");
    case "llm_model_unknown":
      return m("modelUnknown");
    case "llm_request_rejected":
      return m("requestRejected", "retry");
    case "llm_key_unreadable":
      return m("keyUnreadable", "settings");
    case "session_ended":
      return m("sessionEnded", "signIn");
    case "network":
      return m("network", "retry");
  }

  switch (problem.status) {
    case 0:
      return m("network", "retry");
    case 400:
      return m("checkFields");
    case 401:
      return m("sessionEnded", "signIn");
    case 403:
      // A rejected cross-site request is also 403 (no code); the API's own 403 is the unconfirmed email.
      return problem.title === "Forbidden" && problem.detail?.startsWith("Cross-site")
        ? m("forbidden")
        : m("confirmEmail", "resend");
    case 404:
      return m("docGone", "refresh");
    case 409:
      if (ctx.area === "upload") return m("documentLimit", null, { limit: ctx.documentLimit ?? 0 });
      if (ctx.area === "summary" || ctx.area === "compare" || ctx.area === "ask")
        return m("docNotReady", "refresh");
      return m("conflict", "retry");
    case 413:
      return m("tooLarge", null, { size: ctx.maxBytes ?? 0 });
    case 415:
      return m("unsupported");
    case 429:
      if (ctx.area === "ask" || ctx.area === "summary" || ctx.area === "compare")
        return m("maxQuestions", "retry");
      return problem.retryAfter
        ? m("tooMany", "retry", seconds(problem))
        : m("tooManyNoWait", "retry");
    case 500:
      return m("server", "retry");
    case 504:
      return m("tookTooLong", "retry");
    case 502:
    case 503:
      return m("serviceDown", "retry");
  }
  return problem.status >= 500 ? m("server", "retry") : m("generic", "retry");
}
