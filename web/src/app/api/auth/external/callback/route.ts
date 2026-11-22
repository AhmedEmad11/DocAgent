import type { NextRequest } from "next/server";
import { finishExternal } from "@/lib/auth/external";

export async function GET(req: NextRequest) {
  return finishExternal(req);
}
