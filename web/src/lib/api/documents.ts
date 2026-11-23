import { apiJson, type ApiRequestInit, type ApiResult } from "./client";
import { documentListSchema, type ApiDocument } from "./documents-schema";

/** Server-side list for the first render (the browser then refreshes through the allowlisted proxy). */
export const listDocuments = (
  v: Pick<ApiRequestInit, "visitorHeaders">,
  accessToken: string,
): Promise<ApiResult<ApiDocument[]>> =>
  apiJson("/documents", { ...v, accessToken }, (b) => documentListSchema.parse(b));
