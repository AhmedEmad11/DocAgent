import type { NextRequest } from "next/server";
import { startExternal } from "@/lib/auth/external";

export async function GET(req: NextRequest, ctx: RouteContext<"/api/auth/external/[provider]">) {
  const { provider } = await ctx.params;
  return startExternal(req, provider);
}
