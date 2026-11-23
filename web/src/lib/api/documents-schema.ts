import { z } from "zod";

// Pure (no server imports): shared by server code and client components.
export const DOCUMENT_STATUSES = ["Pending", "Processing", "Ready", "Failed"] as const;
export type DocumentStatus = (typeof DOCUMENT_STATUSES)[number];

export const documentSchema = z.object({
  id: z.string(),
  fileName: z.string(),
  contentType: z.string(),
  sizeBytes: z.number(),
  pageCount: z.number().nullable(),
  chunkCount: z.number(),
  status: z.enum(DOCUMENT_STATUSES),
  failureReason: z.string().nullable(),
  extractionNotice: z.string().nullable(),
  createdAt: z.string(),
});
export type ApiDocument = z.infer<typeof documentSchema>;

export const documentListSchema = z.array(documentSchema);
