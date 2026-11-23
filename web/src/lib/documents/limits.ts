// Tier limits (SRS D19, UI-DOC-2, UI-DOC-8). The API stays the authority; these only let the UI refuse early and explain.
export interface TierLimits {
  maxDocuments: number;
  maxFileBytes: number;
}

const MB = 1024 * 1024;
export const DEMO_LIMITS: TierLimits = { maxDocuments: 3, maxFileBytes: 5 * MB };
export const BYOK_LIMITS: TierLimits = { maxDocuments: 100, maxFileBytes: 25 * MB };

export const limitsFor = (hasLlmKey: boolean): TierLimits =>
  hasLlmKey ? BYOK_LIMITS : DEMO_LIMITS;

export const ALLOWED_EXTENSIONS = [".pdf", ".docx", ".txt", ".md"] as const;
export const ACCEPT_ATTRIBUTE = ALLOWED_EXTENSIONS.join(",");

export type FileCheck =
  { ok: true } | { ok: false; reason: "unsupported" | "tooLarge" | "empty" | "documentLimit" };

/** Local pre-check before a file is sent (UI-DOC-2). `slotsLeft` is how many more documents the account may hold. */
export function checkFile(
  file: { name: string; size: number },
  limits: TierLimits,
  slotsLeft: number,
): FileCheck {
  const lower = file.name.toLowerCase();
  if (!ALLOWED_EXTENSIONS.some((ext) => lower.endsWith(ext)))
    return { ok: false, reason: "unsupported" };
  if (file.size === 0) return { ok: false, reason: "empty" };
  if (file.size > limits.maxFileBytes) return { ok: false, reason: "tooLarge" };
  if (slotsLeft <= 0) return { ok: false, reason: "documentLimit" };
  return { ok: true };
}

export type DocumentKind = "pdf" | "docx" | "text" | "markdown" | "other";

export function documentKind(contentType: string): DocumentKind {
  if (contentType === "application/pdf") return "pdf";
  if (contentType.includes("wordprocessingml")) return "docx";
  if (contentType === "text/markdown") return "markdown";
  if (contentType === "text/plain") return "text";
  return "other";
}
