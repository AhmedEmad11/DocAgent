import { createFormatter } from "next-intl";
import { describe, expect, it } from "vitest";
import { findMismatches } from "../../scripts/check-messages.mjs";
import ar from "../../messages/ar.json";
import en from "../../messages/en.json";
import { formats } from "./formats";
import { getDirection, routing } from "./routing";

describe("i18n setup", () => {
  it("defaults to English and supports Arabic", () => {
    expect(routing.defaultLocale).toBe("en");
    expect([...routing.locales]).toEqual(["en", "ar"]);
  });

  it("sets dir by locale", () => {
    expect(getDirection("ar")).toBe("rtl");
    expect(getDirection("en")).toBe("ltr");
  });

  it("has the same message keys in every catalog", () => {
    expect(findMismatches({ en, ar })).toEqual([]);
  });

  it("detects a missing key", () => {
    expect(findMismatches({ en: { a: "1", b: { c: "2" } }, ar: { a: "1" } })).toEqual([
      'ar: missing "b.c"',
    ]);
  });

  it("formats numbers and dates with Western digits in Arabic", () => {
    const f = createFormatter({ locale: "ar", formats });
    expect(f.number(1234567.5, "default")).toMatch(/^[0-9.,٬٫٬٫ ]+$/);
    expect(f.number(1234567.5, "default")).not.toMatch(/[٠-٩]/);
    expect(
      f.dateTime(new Date(Date.UTC(2026, 10, 21, 12)), {
        ...formats.dateTime.long,
        timeZone: "UTC",
      }),
    ).not.toMatch(/[٠-٩]/);
  });
});
