import "server-only";
import { headers } from "next/headers";
import { listDocuments } from "@/lib/api/documents";
import type { ApiDocument } from "@/lib/api/documents-schema";
import { readSession } from "@/lib/session/server";

/** The signed-in user's documents for the first server render of a page. A failure is an error page with a retry (UI-GEN-2). */
export async function loadDocuments(): Promise<ApiDocument[]> {
  const session = await readSession();
  const result = session
    ? await listDocuments({ visitorHeaders: await headers() }, session.at)
    : null;
  if (!result || !result.ok) throw new Error("The documents could not be loaded");
  return result.data;
}
