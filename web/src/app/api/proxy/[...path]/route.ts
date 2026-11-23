import type { NextRequest } from "next/server";
import { handleProxy } from "@/lib/api/proxy-handler";

// Summary, compare and the non-streaming ask run up to the agent timeout (D20: 120 s) plus margin (UI-PERF-4).
export const maxDuration = 180;

type Ctx = RouteContext<"/api/proxy/[...path]">;

async function handle(req: NextRequest, ctx: Ctx) {
  const { path } = await ctx.params;
  return handleProxy(req, path);
}

export const GET = handle;
export const POST = handle;
export const PUT = handle;
export const DELETE = handle;
