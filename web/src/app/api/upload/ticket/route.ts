import type { NextRequest } from "next/server";
import { handleUploadTicket } from "@/lib/api/upload-ticket-handler";

export const POST = (req: NextRequest) => handleUploadTicket(req);
