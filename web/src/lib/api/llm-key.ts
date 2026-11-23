import { z } from "zod";
import { apiJson, type ApiRequestInit, type ApiResult } from "./client";

export const savedKeySchema = z.object({
  provider: z.string(),
  model: z.string(),
  last4: z.string(),
  updatedAt: z.string(),
});
export type SavedKey = z.infer<typeof savedKeySchema>;

/** The saved configuration for the first render of the settings page; null when none is saved (the API answers 404). */
export async function getSavedKey(
  v: Pick<ApiRequestInit, "visitorHeaders">,
  accessToken: string,
): Promise<ApiResult<SavedKey | null>> {
  const result = await apiJson("/me/llm-key", { ...v, accessToken }, (b) =>
    savedKeySchema.parse(b),
  );
  if (!result.ok && result.problem.status === 404) return { ok: true, status: 404, data: null };
  return result;
}
