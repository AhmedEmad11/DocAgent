// Result shape returned by the auth server actions to the client forms. Keys, never raw API text, so nothing from the API is echoed.
export type ActionResult =
  | { ok: true; next?: string }
  | { ok: false; formError?: string; fieldErrors?: Record<string, string[]>; retryAfter?: number };
