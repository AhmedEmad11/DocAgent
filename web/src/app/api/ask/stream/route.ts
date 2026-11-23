import type { NextRequest } from "next/server";
import { handleAskStream } from "@/lib/api/ask-stream-handler";

// UI-PERF-4: above the 120 s BYOK agent timeout plus margin (Hobby allows up to 300 s, verified in 9.0).
export const maxDuration = 180;
export const dynamic = "force-dynamic";

export const POST = (req: NextRequest) => handleAskStream(req);
