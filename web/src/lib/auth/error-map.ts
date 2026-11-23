import type { ApiProblem } from "@/lib/api/problem";

// API errors -> message keys under `Auth.errors.*` (SRS 10.6). Pure so it is unit tested.
// Identity error codes (register, reset): https://learn.microsoft.com/aspnet/core/security/authentication/identity

export interface MappedError {
  /** Message key for the form as a whole. */
  formError?: string;
  /** Field name -> message keys (several password rules can fail at once). */
  fieldErrors?: Record<string, string[]>;
  retryAfter?: number;
}

const PASSWORD_CODES: Record<string, string> = {
  PasswordTooShort: "rule_min",
  PasswordRequiresUpper: "rule_upper",
  PasswordRequiresLower: "rule_lower",
  PasswordRequiresDigit: "rule_digit",
  PasswordRequiresNonAlphanumeric: "rule_symbol",
  PasswordRequiresUniqueChars: "rule_unique",
};

function common(problem: ApiProblem): MappedError | null {
  if (problem.code === "api_starting") return { formError: "apiStarting" };
  if (problem.status === 0 || problem.code === "network") return { formError: "network" };
  if (problem.status === 429) {
    return problem.retryAfter
      ? { formError: "rateLimitedAfter", retryAfter: problem.retryAfter }
      : { formError: "rateLimited" };
  }
  if (problem.status >= 500) return { formError: "server" };
  return null;
}

/** Register: per-rule password messages next to the field; a duplicate email never says the address exists (UI-AUTH-1, SRS 10.6). */
export function mapRegisterError(problem: ApiProblem): MappedError {
  const generic = common(problem);
  if (generic) return generic;
  const codes = Object.keys(problem.errors ?? {});
  const password = codes.flatMap((c) => (PASSWORD_CODES[c] ? [PASSWORD_CODES[c]] : []));
  const fieldErrors: Record<string, string[]> = {};
  if (password.length > 0) fieldErrors.password = password;
  if (codes.some((c) => c === "InvalidEmail" || c === "InvalidUserName"))
    fieldErrors.email = ["emailInvalid"];
  if (codes.some((c) => c === "DuplicateUserName" || c === "DuplicateEmail"))
    return { formError: "registerFailed" };
  if (Object.keys(fieldErrors).length > 0) return { fieldErrors };
  return { formError: "checkFields" };
}

/** Sign-in: the same message for unknown account and wrong password (no enumeration); lockout is called out so the user can act. */
export function mapSignInError(problem: ApiProblem): MappedError {
  const generic = common(problem);
  if (generic) return generic;
  if (problem.status === 401 && problem.detail === "LockedOut") return { formError: "lockedOut" };
  if (problem.status === 401 || problem.status === 400) return { formError: "badCredentials" };
  return { formError: "server" };
}

export function mapResetError(problem: ApiProblem): MappedError {
  const generic = common(problem);
  if (generic) return generic;
  const codes = Object.keys(problem.errors ?? {});
  const password = codes.flatMap((c) => (PASSWORD_CODES[c] ? [PASSWORD_CODES[c]] : []));
  if (password.length > 0) return { fieldErrors: { newPassword: password } };
  if (codes.includes("InvalidToken")) return { formError: "resetInvalid" };
  return { formError: "checkFields" };
}

/** Forgot password and resend confirmation always look successful (D29, UI-AUTH-5); only rate limits and outages surface. */
export function mapQuietError(problem: ApiProblem): MappedError | null {
  return common(problem);
}

export function mapConfirmError(problem: ApiProblem): MappedError {
  const generic = common(problem);
  if (generic) return generic;
  return { formError: "confirmInvalid" };
}

/** `?error=` from the external callback (UI-AUTH-6, AUTH-5) and `?reason=` for ended sessions. */
export const EXTERNAL_ERRORS = [
  "email_not_verified",
  "no_verified_email",
  "external_login_failed",
] as const;
export type ExternalError = (typeof EXTERNAL_ERRORS)[number];

export function toExternalError(value: string | null | undefined): ExternalError {
  return (EXTERNAL_ERRORS as readonly string[]).includes(value ?? "")
    ? (value as ExternalError)
    : "external_login_failed";
}
