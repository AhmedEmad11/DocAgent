import type { Formats } from "next-intl";

// Western digits (0-9) in every locale, including Arabic (SRS UI-I18N-5, OI-14 default).
export const formats = {
  number: {
    default: { numberingSystem: "latn" },
    integer: { numberingSystem: "latn", maximumFractionDigits: 0 },
  },
  dateTime: {
    short: { numberingSystem: "latn", dateStyle: "medium" },
    long: { numberingSystem: "latn", dateStyle: "long", timeStyle: "short" },
  },
} satisfies Formats;
