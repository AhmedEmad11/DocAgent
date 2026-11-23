import { readFileSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import en from "../../../messages/en.json";
import ar from "../../../messages/ar.json";
import { KNOWN_PRIVACY_NOTICE_EN, isKnownPrivacyNotice } from "./privacy";

/** The API's constant, read from its source: three concatenated string literals after `PrivacyNotice =`. */
function apiPrivacyNotice(): string {
  const source = readFileSync(
    join(process.cwd(), "..", "src", "DocAgent.Infrastructure", "Llm", "NoKeyRoute.cs"),
    "utf8",
  );
  const declaration = /PrivacyNotice\s*=\s*((?:"(?:[^"\\]|\\.)*"\s*\+?\s*)+);/.exec(source);
  if (!declaration) throw new Error("PrivacyNotice constant not found in the API source");
  const literals = [...declaration[1]!.matchAll(/"((?:[^"\\]|\\.)*)"/g)].map((m) => m[1]!);
  return literals.join("");
}

describe("demo privacy notice (OI-13)", () => {
  it("the English text the UI knows is exactly the API's (a change there fails this test)", () => {
    expect(apiPrivacyNotice()).toBe(KNOWN_PRIVACY_NOTICE_EN);
  });

  it("the English catalog carries the same text, so English users see the API's wording", () => {
    expect(en.Demo.privacyNotice).toBe(KNOWN_PRIVACY_NOTICE_EN);
  });

  it("the Arabic catalog has a translation of its own", () => {
    expect(ar.Demo.privacyNotice).not.toBe(KNOWN_PRIVACY_NOTICE_EN);
    expect(ar.Demo.privacyNotice).toMatch(/[؀-ۿ]/);
  });

  it("recognises the notice despite surrounding whitespace, and nothing else", () => {
    expect(isKnownPrivacyNotice(`  ${KNOWN_PRIVACY_NOTICE_EN}\n`)).toBe(true);
    expect(isKnownPrivacyNotice(`${KNOWN_PRIVACY_NOTICE_EN} Extra.`)).toBe(false);
    expect(isKnownPrivacyNotice("Something else")).toBe(false);
  });
});
