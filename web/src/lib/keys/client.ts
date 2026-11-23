import { z } from "zod";
import { savedKeySchema, type SavedKey } from "@/lib/api/llm-key";
import { bffFetch, type BffResult } from "@/lib/client/bff";

// Browser calls for the key settings, always to our own BFF (never the API). The key travels in a request header or the JSON body of a
// same-origin call, is held only in the form's state, and is cleared after use. It is never put in a URL, storage or a log (UI-KEY-5, UI-SEC-6).

const modelsSchema = z.object({
  provider: z.string(),
  models: z.array(z.object({ id: z.string(), name: z.string() })),
});
export type ModelInfo = z.infer<typeof modelsSchema>["models"][number];

export const loadModels = (
  provider: string,
  apiKey: string,
  signal?: AbortSignal,
): Promise<BffResult<ModelInfo[]>> =>
  bffFetch(
    `/api/proxy/providers/${encodeURIComponent(provider)}/models`,
    { headers: { "x-llm-api-key": apiKey.trim() }, ...(signal ? { signal } : {}) },
    (b) => modelsSchema.parse(b).models,
  );

export const saveKey = (
  input: { provider: string; model: string; apiKey: string },
  signal?: AbortSignal,
): Promise<BffResult<SavedKey>> =>
  bffFetch(
    "/api/proxy/me/llm-key",
    {
      method: "PUT",
      json: { provider: input.provider, model: input.model.trim(), apiKey: input.apiKey.trim() },
      ...(signal ? { signal } : {}),
    },
    (b) => savedKeySchema.parse(b),
  );

export const deleteKey = (): Promise<BffResult<null>> =>
  bffFetch("/api/proxy/me/llm-key", { method: "DELETE" });
