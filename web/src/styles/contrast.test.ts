import { readFileSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import { contrastRatio, over, parseOklch, readTokens, type Colour } from "./contrast";

const css = readFileSync(join(process.cwd(), "src", "app", "globals.css"), "utf8");
const light = readTokens(css, ":root");
const dark = { ...light, ...readTokens(css, ".dark") };

const colour = (tokens: Record<string, string>, name: string): Colour =>
  parseOklch(tokens[`--${name}`]!);
const solid = (tokens: Record<string, string>, name: string, on = "background") =>
  over(colour(tokens, name), colour(tokens, on));
/** `name` at `alpha` painted on the page background, like `bg-destructive/10`. */
const tint = (tokens: Record<string, string>, name: string, alpha: number): Colour =>
  over({ ...colour(tokens, name), alpha }, solid(tokens, "background"));

// [text token, background token, minimum]: text 4.5:1, large text and UI components 3:1 (UI-A11Y-2).
const TEXT: [string, string][] = [
  ["foreground", "background"],
  ["card-foreground", "card"],
  ["muted-foreground", "background"],
  ["muted-foreground", "muted"],
  ["foreground", "muted"],
  ["accent-foreground", "accent"],
  ["primary-foreground", "primary"],
  ["secondary-foreground", "secondary"],
  ["destructive-foreground", "destructive"],
  ["primary", "background"], // links
  ["primary", "muted"], // links inside notices and the banner
  ["destructive", "background"], // error text
  ["destructive", "muted"],
];

describe.each([
  ["light", light],
  ["dark", dark],
] as const)("design tokens, %s theme (UI-A11Y-2)", (_name, tokens) => {
  it.each(TEXT)("text %s on %s has at least 4.5:1", (fg, bg) => {
    expect(contrastRatio(solid(tokens, fg, bg), solid(tokens, bg))).toBeGreaterThanOrEqual(4.5);
  });

  it("body text on the tinted notice boxes (destructive and primary at 10%) has at least 4.5:1", () => {
    for (const name of ["destructive", "primary"]) {
      const box = tint(tokens, name, 0.1);
      expect(contrastRatio(solid(tokens, "foreground"), box), name).toBeGreaterThanOrEqual(4.5);
    }
  });

  it("input borders, the focus ring and the progress fill have at least 3:1 against what they sit on", () => {
    expect(
      contrastRatio(solid(tokens, "input"), solid(tokens, "background")),
      "input",
    ).toBeGreaterThanOrEqual(3);
    expect(
      contrastRatio(solid(tokens, "ring"), solid(tokens, "background")),
      "ring",
    ).toBeGreaterThanOrEqual(3);
    expect(
      contrastRatio(solid(tokens, "primary"), solid(tokens, "muted")),
      "progress",
    ).toBeGreaterThanOrEqual(3);
    expect(
      contrastRatio(solid(tokens, "destructive"), solid(tokens, "muted")),
      "progress high",
    ).toBeGreaterThanOrEqual(3);
  });
});

describe("contrast helpers", () => {
  it("black on white is 21:1 and the same colour is 1:1", () => {
    const white = parseOklch("oklch(1 0 0)");
    const black = parseOklch("oklch(0 0 0)");
    expect(contrastRatio(black, white)).toBeCloseTo(21, 0);
    expect(contrastRatio(white, white)).toBeCloseTo(1, 5);
  });

  it("reads alpha and composites it", () => {
    const half = parseOklch("oklch(0 0 0 / 50%)");
    expect(half.alpha).toBe(0.5);
    const grey = over(half, parseOklch("oklch(1 0 0)"));
    expect(grey.rgb[0]).toBeCloseTo(0.5, 1);
  });
});
