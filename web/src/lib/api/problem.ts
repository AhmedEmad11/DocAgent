// Problem Details (RFC 9457) as the API sends them, normalised to the fixed shape the UI works with (SRS 10.6).
export interface ApiProblem {
  status: number;
  title: string;
  detail?: string;
  /** Machine-readable code (`demo_quota_exceeded`, ...). */
  code?: string;
  retryAfter?: number;
  /** Identity validation problems (register, reset): error code -> messages. */
  errors?: Record<string, string[]>;
}

const STATUS_TITLES: Record<number, string> = {
  400: "Bad request",
  401: "Unauthorized",
  403: "Forbidden",
  404: "Not found",
  409: "Conflict",
  413: "Payload too large",
  415: "Unsupported media type",
  429: "Too many requests",
  500: "Server error",
  502: "Bad gateway",
  503: "Service unavailable",
  504: "Gateway timeout",
};

function str(v: unknown): string | undefined {
  return typeof v === "string" && v.length > 0 ? v : undefined;
}

function parseRetryAfter(header: string | null, body: unknown): number | undefined {
  const fromBody = typeof body === "number" ? body : Number(body);
  if (Number.isFinite(fromBody) && fromBody > 0) return Math.ceil(fromBody);
  const fromHeader = header === null ? NaN : Number(header);
  return Number.isFinite(fromHeader) && fromHeader > 0 ? Math.ceil(fromHeader) : undefined;
}

/** Never throws: an unreadable body still yields a problem with the status. Tokens and request bodies are never copied. */
export async function parseProblem(res: Response): Promise<ApiProblem> {
  let body: Record<string, unknown> = {};
  try {
    const text = await res.text();
    if (text) {
      const parsed: unknown = JSON.parse(text);
      if (parsed !== null && typeof parsed === "object") body = parsed as Record<string, unknown>;
    }
  } catch {
    // not JSON (plain text or empty)
  }
  const problem: ApiProblem = {
    status: res.status,
    title: str(body.title) ?? STATUS_TITLES[res.status] ?? "Error",
  };
  const detail = str(body.detail);
  if (detail) problem.detail = detail;
  const code = str(body.code);
  if (code) problem.code = code;
  const retryAfter = parseRetryAfter(res.headers.get("retry-after"), body.retryAfter);
  if (retryAfter !== undefined) problem.retryAfter = retryAfter;

  if (body.errors !== null && typeof body.errors === "object" && !Array.isArray(body.errors)) {
    const errors: Record<string, string[]> = {};
    for (const [key, value] of Object.entries(body.errors as Record<string, unknown>)) {
      if (Array.isArray(value))
        errors[key] = value.filter((m): m is string => typeof m === "string");
    }
    if (Object.keys(errors).length > 0) problem.errors = errors;
  }
  return problem;
}

/** The BFF could not reach the API (offline, cold start, timeout). */
export const NETWORK_PROBLEM: ApiProblem = {
  status: 0,
  title: "Cannot reach the service",
  code: "network",
};
