import { describe, expect, it } from "vitest";
import { BYOK_LIMITS, DEMO_LIMITS, checkFile, documentKind, limitsFor } from "./limits";
import { formatBytes } from "./format";

const MB = 1024 * 1024;

describe("tier limits (D19)", () => {
  it("demo: 3 documents of 5 MB; own key: 100 documents of 25 MB", () => {
    expect(limitsFor(false)).toEqual(DEMO_LIMITS);
    expect(limitsFor(true)).toEqual(BYOK_LIMITS);
    expect(DEMO_LIMITS).toEqual({ maxDocuments: 3, maxFileBytes: 5 * MB });
    expect(BYOK_LIMITS).toEqual({ maxDocuments: 100, maxFileBytes: 25 * MB });
  });
});

describe("checkFile (UI-DOC-2)", () => {
  const ok = (name: string, size = 1000, slots = 1, limits = DEMO_LIMITS) =>
    checkFile({ name, size }, limits, slots);

  it("accepts the four types in any letter case", () => {
    for (const name of ["a.pdf", "b.DOCX", "c.Txt", "d.md", "عقد.pdf", "a b.c.pdf"])
      expect(ok(name)).toEqual({ ok: true });
  });

  it("refuses other types by extension", () => {
    for (const name of ["a.exe", "a.doc", "a.pdf.exe", "pdf", "a.png", "noextension"])
      expect(ok(name)).toEqual({ ok: false, reason: "unsupported" });
  });

  it("refuses empty files and files over the tier size, accepts exactly the limit", () => {
    expect(ok("a.txt", 0)).toEqual({ ok: false, reason: "empty" });
    expect(ok("a.txt", 5 * MB + 1)).toEqual({ ok: false, reason: "tooLarge" });
    expect(ok("a.txt", 5 * MB)).toEqual({ ok: true });
    expect(ok("a.txt", 20 * MB, 1, BYOK_LIMITS)).toEqual({ ok: true });
    expect(ok("a.txt", 25 * MB + 1, 1, BYOK_LIMITS)).toEqual({ ok: false, reason: "tooLarge" });
  });

  it("refuses when no document slot is left", () => {
    expect(ok("a.txt", 10, 0)).toEqual({ ok: false, reason: "documentLimit" });
  });

  it("reports the type problem before the size problem", () => {
    expect(ok("a.exe", 100 * MB)).toEqual({ ok: false, reason: "unsupported" });
  });
});

describe("documentKind", () => {
  it("maps the API content types", () => {
    expect(documentKind("application/pdf")).toBe("pdf");
    expect(
      documentKind("application/vnd.openxmlformats-officedocument.wordprocessingml.document"),
    ).toBe("docx");
    expect(documentKind("text/plain")).toBe("text");
    expect(documentKind("text/markdown")).toBe("markdown");
    expect(documentKind("")).toBe("other");
  });
});

describe("formatBytes (UI-I18N-5)", () => {
  it("uses the active locale's units and Western digits in both languages", () => {
    expect(formatBytes(500, "en")).toBe("500 byte");
    expect(formatBytes(5 * MB, "en")).toBe("5 MB");
    expect(formatBytes(1536, "en")).toBe("1.5 kB");
    const ar = formatBytes(5 * MB, "ar");
    expect(ar).toMatch(/^5 /);
    expect(ar).toMatch(/[؀-ۿ]/); // Arabic unit name
    expect(ar).not.toMatch(/[٠-٩]/); // no Arabic-Indic digits
    expect(formatBytes(25 * MB, "ar")).toMatch(/^25 /);
  });
});
