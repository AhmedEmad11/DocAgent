import { describe, expect, it } from "vitest";
import ar from "../../../messages/ar.json";
import en from "../../../messages/en.json";
import type { ApiProblem } from "@/lib/api/problem";
import { ERROR_KEYS, mapProblem, type ProblemContext } from "./problem-message";

const p = (status: number, extra: Partial<ApiProblem> = {}): ApiProblem => ({
  status,
  title: "t",
  ...extra,
});

// SRS 10.6, row by row.
const cases: [string, ApiProblem, ProblemContext, string, string | null][] = [
  ["demo_quota_exceeded", p(429, { code: "demo_quota_exceeded" }), {}, "demoQuota", "settings"],
  [
    "demo_rate_limited",
    p(429, { code: "demo_rate_limited", retryAfter: 9 }),
    {},
    "demoBusy",
    "retry",
  ],
  ["demo_unavailable", p(503, { code: "demo_unavailable" }), {}, "demoUnavailable", "settings"],
  ["llm_key_required", p(403, { code: "llm_key_required" }), {}, "keyRequired", "settings"],
  [
    "llm_key_invalid on save",
    p(400, { code: "llm_key_invalid" }),
    { area: "key-save" },
    "keyInvalidSave",
    null,
  ],
  [
    "llm_key_invalid in use",
    p(422, { code: "llm_key_invalid" }),
    { area: "ask" },
    "keyInvalidUse",
    "settings",
  ],
  [
    "llm_no_credit",
    p(402, { code: "llm_no_credit" }),
    { provider: "OpenAI" },
    "noCredit",
    "settings",
  ],
  [
    "llm_rate_limited",
    p(429, { code: "llm_rate_limited", retryAfter: 5 }),
    {},
    "providerLimit",
    "retry",
  ],
  ["llm_unavailable", p(503, { code: "llm_unavailable" }), {}, "providerDown", "retry"],
  ["llm_model_unknown 400", p(400, { code: "llm_model_unknown" }), {}, "modelUnknown", null],
  ["llm_model_unknown 422", p(422, { code: "llm_model_unknown" }), {}, "modelUnknown", null],
  [
    "llm_request_rejected",
    p(502, { code: "llm_request_rejected" }),
    {},
    "requestRejected",
    "retry",
  ],
  ["llm_key_unreadable", p(409, { code: "llm_key_unreadable" }), {}, "keyUnreadable", "settings"],
  ["400 without a code", p(400), {}, "checkFields", null],
  ["401", p(401), {}, "sessionEnded", "signIn"],
  ["403 without a code (unconfirmed)", p(403), {}, "confirmEmail", "resend"],
  ["404", p(404), {}, "docGone", "refresh"],
  [
    "409 at the document limit",
    p(409),
    { area: "upload", documentLimit: 3 },
    "documentLimit",
    null,
  ],
  ["409 not ready (summary)", p(409), { area: "summary" }, "docNotReady", "refresh"],
  ["409 not ready (compare)", p(409), { area: "compare" }, "docNotReady", "refresh"],
  ["409 key conflict", p(409), { area: "key" }, "conflict", "retry"],
  ["413", p(413), { maxBytes: 5 * 1024 * 1024 }, "tooLarge", null],
  ["415", p(415), {}, "unsupported", null],
  ["429 with Retry-After", p(429, { retryAfter: 12 }), { area: "key" }, "tooMany", "retry"],
  ["429 without a wait", p(429), { area: "upload" }, "tooManyNoWait", "retry"],
  ["429 on ask (concurrency)", p(429, { retryAfter: 2 }), { area: "ask" }, "maxQuestions", "retry"],
  ["500", p(500), {}, "server", "retry"],
  ["502 without a code", p(502), {}, "serviceDown", "retry"],
  ["503 without a code", p(503), {}, "serviceDown", "retry"],
  ["503 api_starting (cold start)", p(503, { code: "api_starting" }), {}, "apiStarting", "retry"],
  ["504", p(504), {}, "tookTooLong", "retry"],
  ["network (BFF cannot reach the API)", p(0, { code: "network" }), {}, "network", "retry"],
  ["session_ended from the BFF", p(401, { code: "session_ended" }), {}, "sessionEnded", "signIn"],
  [
    "cross-site rejection",
    p(403, { title: "Forbidden", detail: "Cross-site request rejected." }),
    {},
    "forbidden",
    null,
  ],
];

describe("mapProblem (SRS 10.6)", () => {
  it.each(cases)("%s", (_name, problem, ctx, key, action) => {
    const mapped = mapProblem(problem, ctx);
    expect(mapped.key).toBe(key);
    expect(mapped.action).toBe(action);
  });

  it("carries the wait, the limit and the size as ICU values", () => {
    expect(mapProblem(p(429, { retryAfter: 12 }), {}).values).toEqual({ seconds: 12 });
    expect(mapProblem(p(409), { area: "upload", documentLimit: 3 }).values).toEqual({ limit: 3 });
    expect(mapProblem(p(413), { maxBytes: 5242880 }).values).toEqual({ size: 5242880 });
  });

  it("asks the renderer for the reset time only on the demo quota", () => {
    expect(mapProblem(p(429, { code: "demo_quota_exceeded" })).needsTime).toBe(true);
    expect(mapProblem(p(429, { code: "demo_rate_limited" })).needsTime).toBeUndefined();
  });

  it("falls back to a generic message for statuses the table does not list", () => {
    expect(mapProblem(p(418)).key).toBe("generic");
    expect(mapProblem(p(599)).key).toBe("server");
  });

  it("every key it can return has a message in English and in Arabic", () => {
    const errors = (c: typeof en) => c.Errors as Record<string, unknown>;
    for (const key of ERROR_KEYS) {
      expect(errors(en)[key], `en ${key}`).toEqual(expect.any(String));
      expect(errors(ar)[key], `ar ${key}`).toEqual(expect.any(String));
    }
    const mappedKeys = new Set(cases.map((c) => c[3]));
    for (const k of mappedKeys) expect(ERROR_KEYS).toContain(k);
  });

  it("every action the table names has a label in both languages", () => {
    const actions = new Set(cases.map((c) => c[4]).filter(Boolean) as string[]);
    for (const a of actions) {
      expect((en.Errors.actions as Record<string, string>)[a], `en ${a}`).toBeTruthy();
      expect((ar.Errors.actions as Record<string, string>)[a], `ar ${a}`).toBeTruthy();
    }
  });
});
