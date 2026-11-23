import { NextResponse, type NextRequest } from "next/server";
import { z } from "zod";
import { getServerEnv } from "@/lib/env";
import { isSameOrigin } from "@/lib/auth/origin";
import { authedApiFetch } from "@/lib/session/server";
import { parseProblem, type ApiProblem } from "./problem";

const NO_STORE = { "cache-control": "no-store" } as const;
const ticketSchema = z.object({ ticket: z.string().min(16), expiresInSeconds: z.number() });

function problem(p: ApiProblem): NextResponse {
  const headers: Record<string, string> = { ...NO_STORE };
  if (p.retryAfter) headers["retry-after"] = String(p.retryAfter);
  return NextResponse.json(p, { status: p.status || 502, headers });
}

/**
 * `POST /api/upload/ticket` (plan A3, D40): asks the API for a one-time upload ticket with the session's token and hands the browser the
 * ticket and the API's upload URL. The browser then posts the file straight to the API (the only call it makes there); it never sees
 * an access or refresh token. The ticket works for one upload within 60 seconds and nothing else.
 */
export async function handleUploadTicket(req: NextRequest): Promise<NextResponse> {
  const env = getServerEnv();
  if (!isSameOrigin(req.headers, env.NEXT_PUBLIC_SITE_URL))
    return problem({ status: 403, title: "Forbidden", detail: "Cross-site request rejected." });

  const result = await authedApiFetch("/documents/upload-ticket", { method: "POST" });
  if (result.kind === "anonymous")
    return problem({ status: 401, title: "Unauthorized", code: "session_ended" });
  if (result.kind === "network")
    return problem({ status: 503, title: "Cannot reach the service", code: "network" });
  if (!result.res.ok) return problem(await parseProblem(result.res));

  try {
    const body = ticketSchema.parse(await result.res.json());
    return NextResponse.json(
      {
        ticket: body.ticket,
        expiresInSeconds: body.expiresInSeconds,
        uploadUrl: `${env.DOCAGENT_API_URL}/api/v1/documents`,
      },
      { headers: NO_STORE },
    );
  } catch {
    return problem({ status: 502, title: "Unexpected response from the service" });
  }
}
