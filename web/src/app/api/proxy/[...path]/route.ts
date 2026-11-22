import type { NextRequest } from "next/server";
import { handleProxy } from "@/lib/api/proxy-handler";

type Ctx = RouteContext<"/api/proxy/[...path]">;

async function handle(req: NextRequest, ctx: Ctx) {
  const { path } = await ctx.params;
  return handleProxy(req, path);
}

export const GET = handle;
export const POST = handle;
export const PUT = handle;
export const DELETE = handle;
