// Key form rules, mirroring the API's (LlmKeyEndpoints): the API validates again and with a live call to the provider.
export const PROVIDERS = ["openrouter", "anthropic", "openai", "gemini"] as const;
export type Provider = (typeof PROVIDERS)[number];

export const MIN_KEY = 8;
export const MAX_KEY = 512;
export const MAX_MODEL = 200;

const KEY_PATTERN = /^[\x21-\x7E]+$/;
const MODEL_PATTERN = /^[A-Za-z0-9][A-Za-z0-9._:/-]*$/;

export type KeyField = "key" | "model";
export type KeyFieldError =
  "keyRequired" | "keyFormat" | "keyShort" | "keyLong" | "modelRequired" | "modelFormat";

export function validateKeyForm(input: {
  provider: string;
  key: string;
  model: string;
}): Partial<Record<KeyField, KeyFieldError>> {
  const errors: Partial<Record<KeyField, KeyFieldError>> = {};
  const key = input.key.trim();
  if (!key) errors.key = "keyRequired";
  else if (key.length < MIN_KEY) errors.key = "keyShort";
  else if (key.length > MAX_KEY) errors.key = "keyLong";
  else if (!KEY_PATTERN.test(key)) errors.key = "keyFormat";
  const model = input.model.trim();
  if (!model) errors.model = "modelRequired";
  else if (model.length > MAX_MODEL || !MODEL_PATTERN.test(model)) errors.model = "modelFormat";
  return errors;
}

/** A key can be used to list models only when it would pass the same checks (no model needed). */
export const canListModels = (key: string): boolean =>
  validateKeyForm({ provider: "", key, model: "x" }).key === undefined;
