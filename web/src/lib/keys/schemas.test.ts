import { describe, expect, it } from "vitest";
import { MAX_KEY, MAX_MODEL, PROVIDERS, canListModels, validateKeyForm } from "./schemas";

const ok = { provider: "openrouter", key: "sk-or-v1-abcdef123456", model: "openai/gpt-4.1-mini" };

describe("key form rules (mirror the API)", () => {
  it("offers the four providers of the API", () => {
    expect([...PROVIDERS]).toEqual(["openrouter", "anthropic", "openai", "gemini"]);
  });

  it("accepts a normal key and model", () => {
    expect(validateKeyForm(ok)).toEqual({});
  });

  it("checks the key: required, 8 to 512 printable characters, no spaces", () => {
    expect(validateKeyForm({ ...ok, key: "" }).key).toBe("keyRequired");
    expect(validateKeyForm({ ...ok, key: "   " }).key).toBe("keyRequired");
    expect(validateKeyForm({ ...ok, key: "short" }).key).toBe("keyShort");
    expect(validateKeyForm({ ...ok, key: "x".repeat(MAX_KEY + 1) }).key).toBe("keyLong");
    expect(validateKeyForm({ ...ok, key: "x".repeat(MAX_KEY) }).key).toBeUndefined();
    expect(validateKeyForm({ ...ok, key: "has a space inside" }).key).toBe("keyFormat");
    expect(validateKeyForm({ ...ok, key: "ключ-кириллица-key" }).key).toBe("keyFormat");
    expect(validateKeyForm({ ...ok, key: "  sk-or-v1-abcdef  " }).key).toBeUndefined(); // surrounding spaces are trimmed
  });

  it("checks the model: required, at most 200, letters digits . _ - : /", () => {
    expect(validateKeyForm({ ...ok, model: "" }).model).toBe("modelRequired");
    expect(validateKeyForm({ ...ok, model: "-bad" }).model).toBe("modelFormat");
    expect(validateKeyForm({ ...ok, model: "has space" }).model).toBe("modelFormat");
    expect(validateKeyForm({ ...ok, model: "a".repeat(MAX_MODEL + 1) }).model).toBe("modelFormat");
    for (const model of [
      "gpt-4.1",
      "anthropic/claude-3.5:beta",
      "models/gemini-2.0-flash",
      "llama3.1:8b",
    ])
      expect(validateKeyForm({ ...ok, model }).model).toBeUndefined();
  });

  it("listing models needs only a well-formed key", () => {
    expect(canListModels("sk-or-v1-abcdef")).toBe(true);
    expect(canListModels("short")).toBe(false);
    expect(canListModels("")).toBe(false);
  });
});
