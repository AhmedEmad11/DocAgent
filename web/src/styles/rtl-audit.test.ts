import { readdirSync, readFileSync, statSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";

// UI-I18N-3: layout uses CSS logical properties (`ms-*`, `me-*`, `ps-*`, `pe-*`, `start-*`, `end-*`, `text-start`, `border-s`, `rounded-s`),
// never left/right, so Arabic mirrors without per-component fixes. This guards every source file against a physical utility sneaking in.
const root = join(process.cwd(), "src");
function files(dir: string): string[] {
  return readdirSync(dir).flatMap((name) => {
    const path = join(dir, name);
    if (statSync(path).isDirectory()) return files(path);
    return /\.(tsx?|css)$/.test(name) && !/\.test\./.test(name) ? [path] : [];
  });
}

const PHYSICAL_UTILITY =
  /(?:^|[\s"'`])(?:[a-z0-9-]+:)*-?(?:ml|mr|pl|pr|left|right|text-left|text-right|float-left|float-right|clear-left|clear-right|border-l|border-r|rounded-l|rounded-r|rounded-tl|rounded-tr|rounded-bl|rounded-br|scroll-ml|scroll-mr|scroll-pl|scroll-pr)(?:-[\w./[\]%-]+)?(?=[\s"'`]|$)/gm;
const PHYSICAL_CSS =
  /\b(?:margin|padding|border)-(?:left|right)\b|(?:^|[\s;{])(?:left|right)\s*:|text-align\s*:\s*(?:left|right)|float\s*:\s*(?:left|right)/gm;

describe("RTL audit (UI-I18N-3)", () => {
  const sources = files(root);

  it("finds the source files", () => {
    expect(sources.length).toBeGreaterThan(60);
  });

  it.each(sources.map((f) => [f.slice(root.length + 1), f]))(
    "%s has no physical left/right layout",
    (_name, file) => {
      const isCss = (file as string).endsWith(".css");
      const text = readFileSync(file as string, "utf8");
      const code = text.replace(/\/\*[\s\S]*?\*\//g, "").replace(/^\s*\/\/.*$/gm, "");
      // In TypeScript only string and template literals can hold class names (a variable called `left` is fine).
      const literals = isCss
        ? [code]
        : [...code.matchAll(/"[^"\n]*"|'[^'\n]*'|`[^`]*`/g)].map((m) => m[0]);
      expect(
        literals.flatMap((l) => [...l.matchAll(PHYSICAL_UTILITY)].map((m) => m[0].trim())),
      ).toEqual([]);
      if (isCss) expect([...code.matchAll(PHYSICAL_CSS)].map((m) => m[0].trim())).toEqual([]);
    },
  );

  it("the audit pattern does catch physical utilities", () => {
    for (const bad of [
      'className="ml-2"',
      'className="flex pr-4"',
      'className="sm:text-left"',
      'className="absolute left-0"',
      'className="hover:border-r"',
      'className="-mr-1"',
    ]) {
      expect([...bad.matchAll(PHYSICAL_UTILITY)].length, bad).toBeGreaterThan(0);
    }
    for (const good of [
      'className="ms-2 pe-4 text-start start-0 border-s rounded-s"',
      'className="inline-size-full"',
    ]) {
      expect([...good.matchAll(PHYSICAL_UTILITY)].length, good).toBe(0);
    }
  });
});
