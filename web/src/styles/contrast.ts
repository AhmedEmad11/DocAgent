// WCAG contrast of the design tokens in globals.css (UI-A11Y-2). Pure helpers so a unit test can check every pair in both themes.
export type Rgb = [number, number, number];
export interface Colour {
  rgb: Rgb;
  alpha: number;
}

/** `oklch(L C H)` or `oklch(L C H / A%)` to sRGB (0 to 1 per channel, clipped), through OKLab. */
export function parseOklch(value: string): Colour {
  const m = /oklch\(\s*([\d.]+)\s+([\d.]+)\s+([\d.]+)(?:\s*\/\s*([\d.]+)(%?))?\s*\)/.exec(
    value.trim(),
  );
  if (!m) throw new Error(`Not an oklch colour: ${value}`);
  const [L, C, H] = [Number(m[1]), Number(m[2]), (Number(m[3]) * Math.PI) / 180];
  const alpha = m[4] === undefined ? 1 : m[5] ? Number(m[4]) / 100 : Number(m[4]);
  const a = C * Math.cos(H);
  const b = C * Math.sin(H);
  const l = (L + 0.3963377774 * a + 0.2158037573 * b) ** 3;
  const mm = (L - 0.1055613458 * a - 0.0638541728 * b) ** 3;
  const s = (L - 0.0894841775 * a - 1.291485548 * b) ** 3;
  const lin: Rgb = [
    4.0767416621 * l - 3.3077115913 * mm + 0.2309699292 * s,
    -1.2684380046 * l + 2.6097574011 * mm - 0.3413193965 * s,
    -0.0041960863 * l - 0.7034186147 * mm + 1.707614701 * s,
  ];
  const gamma = (x: number) => {
    const c = Math.min(1, Math.max(0, x));
    return c <= 0.0031308 ? 12.92 * c : 1.055 * c ** (1 / 2.4) - 0.055;
  };
  return { rgb: [gamma(lin[0]), gamma(lin[1]), gamma(lin[2])], alpha };
}

/** `top` painted over an opaque `bottom`. */
export function over(top: Colour, bottom: Colour): Colour {
  const a = top.alpha;
  return {
    rgb: [0, 1, 2].map((i) => top.rgb[i]! * a + bottom.rgb[i]! * (1 - a)) as Rgb,
    alpha: 1,
  };
}

const channel = (c: number) => (c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4);
const luminance = ({ rgb }: Colour) =>
  0.2126 * channel(rgb[0]) + 0.7152 * channel(rgb[1]) + 0.0722 * channel(rgb[2]);

export function contrastRatio(a: Colour, b: Colour): number {
  const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x) as [number, number];
  return (hi + 0.05) / (lo + 0.05);
}

/** Custom properties declared in one CSS block, e.g. `:root` or `.dark`. */
export function readTokens(css: string, selector: string): Record<string, string> {
  const start = css.indexOf(`${selector} {`);
  if (start < 0) throw new Error(`No ${selector} block`);
  const end = css.indexOf("}", start);
  const tokens: Record<string, string> = {};
  for (const m of css.slice(start, end).matchAll(/(--[\w-]+):\s*([^;]+);/g))
    tokens[m[1]!] = m[2]!.trim();
  return tokens;
}
