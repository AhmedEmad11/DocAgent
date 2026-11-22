import { locales, type Locale } from "@/i18n/routing";

const authPages = [
  "sign-in",
  "register",
  "check-email",
  "confirm",
  "forgot-password",
  "reset-password",
];

/**
 * Post-sign-in redirect target (UI-SEC-10): only a relative path on this site. Anything else (other origin,
 * protocol-relative, backslashes, control characters, auth pages, unknown locale) becomes the default.
 * A path without a locale prefix gets the current locale.
 */
export function safeNextPath(raw: string | null | undefined, locale: Locale): string {
  const fallback = `/${locale}/documents`;
  if (!raw || raw.length > 512) return fallback;
  if (/[\u0000-\u001f\u007f\\]/.test(raw)) return fallback;
  if (!raw.startsWith("/") || raw.startsWith("//")) return fallback;

  // Dot segments and encoded separators are refused outright (URL parsing would silently resolve them).
  const rawPath = raw.split(/[?#]/)[0] ?? "";
  if (/%2f|%5c/i.test(rawPath)) return fallback;
  for (const part of rawPath.split("/")) {
    let decoded = part;
    try {
      decoded = decodeURIComponent(part);
    } catch {
      return fallback;
    }
    if (decoded === "." || decoded === "..") return fallback;
  }

  let url: URL;
  try {
    url = new URL(raw, "http://site.invalid");
  } catch {
    return fallback;
  }
  if (url.origin !== "http://site.invalid") return fallback;

  const segments = url.pathname.split("/").filter(Boolean);
  let path = url.pathname;
  const first = segments[0];
  if (first && (locales as readonly string[]).includes(first)) {
    segments.shift();
  } else {
    path = `/${locale}${url.pathname === "/" ? "" : url.pathname}`;
  }
  if (segments[0] && authPages.includes(segments[0])) return fallback;
  if (segments[0] === "api") return fallback;
  return `${path}${url.search}`;
}
