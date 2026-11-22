// Edits a request `Cookie` header so the proxy can hand a refreshed (or cleared) session to the rest of the same request.
function parse(header: string | null): [string, string][] {
  if (!header) return [];
  return header
    .split(";")
    .map((p) => p.trim())
    .filter(Boolean)
    .map((p) => {
      const i = p.indexOf("=");
      return i < 0 ? [p, ""] : [p.slice(0, i), p.slice(i + 1)];
    });
}

export function setCookieInHeader(header: string | null, name: string, value: string): string {
  const rest = parse(header).filter(([n]) => n !== name);
  return [...rest, [name, value]].map(([n, v]) => `${n}=${v}`).join("; ");
}

export function removeCookieFromHeader(header: string | null, name: string): string {
  return parse(header)
    .filter(([n]) => n !== name)
    .map(([n, v]) => `${n}=${v}`)
    .join("; ");
}
