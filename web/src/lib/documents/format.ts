// Sizes with `Intl` in the active locale, Western digits (UI-I18N-5, OI-14).
const UNITS = ["byte", "kilobyte", "megabyte", "gigabyte"] as const;

export function formatBytes(bytes: number, locale: string): string {
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < UNITS.length - 1) {
    value /= 1024;
    unit++;
  }
  return new Intl.NumberFormat(locale, {
    style: "unit",
    unit: UNITS[unit],
    unitDisplay: "short",
    numberingSystem: "latn",
    maximumFractionDigits: unit === 0 ? 0 : value < 10 ? 1 : 0,
  }).format(value);
}
