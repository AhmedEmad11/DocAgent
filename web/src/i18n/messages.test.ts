import { describe, expect, it } from "vitest";
import ar from "../../messages/ar.json";
import en from "../../messages/en.json";
import {
  findMismatches,
  findPlaceholderMismatches,
  flattenKeys,
  placeholders,
} from "../../scripts/check-messages.mjs";

type Catalog = Record<string, unknown>;
const flat = (obj: Catalog, prefix = ""): [string, string][] =>
  Object.entries(obj).flatMap(([k, v]) =>
    v !== null && typeof v === "object"
      ? flat(v as Catalog, `${prefix}${k}.`)
      : [[`${prefix}${k}`, String(v)] as [string, string]],
  );

const plurals = (catalog: Catalog) =>
  flat(catalog).filter(([, v]) => /\{\s*\w+\s*,\s*plural\s*,/.test(v));
const categories = (message: string) =>
  new Set([...message.matchAll(/\b(zero|one|two|few|many|other)\s*\{/g)].map((m) => m[1]));

describe("message catalogs (UI-I18N-1, UI-I18N-5, OI-14)", () => {
  it("have the same keys in English and Arabic", () => {
    expect(findMismatches({ en, ar })).toEqual([]);
    expect(flattenKeys(en).length).toBeGreaterThan(300);
  });

  it("use the same placeholders and tags in both languages", () => {
    expect(findPlaceholderMismatches({ en, ar })).toEqual([]);
  });

  it("the placeholder check does catch a broken translation", () => {
    expect(
      placeholders("Hello {name}, <b>{count, plural, one {# item} other {# items}}</b>"),
    ).toEqual(["<b>", "count", "name"]);
    expect(
      findPlaceholderMismatches({ en: { a: "Hi {name}" }, ar: { a: "مرحبا {nome}" } }),
    ).toHaveLength(1);
    expect(
      findPlaceholderMismatches({ en: { a: "Hi <b>{name}</b>" }, ar: { a: "مرحبا {name}" } }),
    ).toHaveLength(1);
  });

  it("English plurals have one and other; Arabic plurals have all six forms (zero, one, two, few, many, other)", () => {
    expect(plurals(en).length).toBeGreaterThan(8);
    for (const [key, message] of plurals(en)) {
      const c = categories(message);
      expect(c.has("one") && c.has("other"), `en ${key}`).toBe(true);
    }
    expect(plurals(ar).length).toBe(plurals(en).length);
    for (const [key, message] of plurals(ar)) {
      const c = categories(message);
      for (const form of ["zero", "one", "two", "few", "many", "other"])
        expect(c.has(form), `ar ${key} needs ${form}`).toBe(true);
    }
  });

  it("Arabic plural forms pick the right text for 0, 1, 2, 3 to 10, 11 to 99 and 100 (CLDR)", () => {
    const rules = new Intl.PluralRules("ar");
    const picks = [0, 1, 2, 3, 10, 11, 99, 100, 102, 103].map((n) => rules.select(n));
    expect(picks).toEqual([
      "zero",
      "one",
      "two",
      "few",
      "few",
      "many",
      "many",
      "other",
      "other",
      "few",
    ]);
  });

  it("the Arabic texts use Western digits only (OI-14)", () => {
    for (const [key, message] of flat(ar)) expect(message, key).not.toMatch(/[٠-٩۰-۹]/);
  });

  it("no message is empty or still an untranslated copy where the English has Latin words only (Arabic has Arabic letters or is a brand/number)", () => {
    const allowed = new Set([
      "Language.en",
      "Settings.providers.openrouter",
      "Settings.providers.anthropic",
      "Settings.providers.openai",
      "Settings.providers.gemini",
      "Common.wordmark",
      "Meta.title",
      "Meta.titleTemplate",
      "Documents.list.kind.pdf",
      "Documents.status.announce",
      "Documents.list.kind.docx",
      "Documents.list.kind.markdown",
      "Ask.counter",
      "Workflow.counter",
      "Language.ar",
    ]);
    for (const [key, message] of flat(ar)) {
      expect(message.trim().length, key).toBeGreaterThan(0);
      if (allowed.has(key)) continue;
      expect(
        /[؀-ۿ]/.test(message) || !/[A-Za-z]{4,}/.test(message),
        `ar ${key} looks untranslated: ${message}`,
      ).toBe(true);
    }
  });
});
