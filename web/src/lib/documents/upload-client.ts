import { NETWORK_PROBLEM, type ApiProblem } from "@/lib/api/problem";
import { problemFromBody } from "@/lib/client/bff";

// Browser side of the upload (plan A3, D40). The browser asks our own BFF for a one-time ticket, then posts the file straight
// to the API with `X-Upload-Ticket`. The ticket is a 60 s, single-use, upload-only credential; the access and refresh tokens
// never leave the server. XMLHttpRequest, because `fetch` cannot report upload progress.

export interface UploadedDocument {
  id: string;
  fileName: string;
  status: string;
}
export type UploadOutcome =
  { ok: true; document: UploadedDocument } | { ok: false; problem: ApiProblem };

export interface TicketResponse {
  ticket: string;
  uploadUrl: string;
  expiresInSeconds: number;
}

export async function requestTicket(
  signal?: AbortSignal,
): Promise<{ ok: true; ticket: TicketResponse } | { ok: false; problem: ApiProblem }> {
  try {
    const res = await fetch("/api/upload/ticket", {
      method: "POST",
      credentials: "same-origin",
      cache: "no-store",
      ...(signal ? { signal } : {}),
    });
    const text = await res.text();
    if (!res.ok)
      return {
        ok: false,
        problem: problemFromBody(res.status, text, res.headers.get("retry-after")),
      };
    const body = JSON.parse(text) as Partial<TicketResponse>;
    if (typeof body.ticket !== "string" || typeof body.uploadUrl !== "string")
      return { ok: false, problem: { status: 502, title: "Unexpected response from the service" } };
    return { ok: true, ticket: body as TicketResponse };
  } catch {
    return { ok: false, problem: NETWORK_PROBLEM };
  }
}

export function postFile(
  file: File,
  ticket: TicketResponse,
  onProgress: (fraction: number) => void,
  signal?: AbortSignal,
): Promise<UploadOutcome> {
  return new Promise((resolve) => {
    const xhr = new XMLHttpRequest();
    xhr.open("POST", ticket.uploadUrl);
    xhr.setRequestHeader("X-Upload-Ticket", ticket.ticket);
    xhr.setRequestHeader("Accept", "application/json");
    xhr.upload.onprogress = (e) => {
      if (e.lengthComputable && e.total > 0) onProgress(Math.min(1, e.loaded / e.total));
    };
    xhr.onerror = () => resolve({ ok: false, problem: NETWORK_PROBLEM });
    xhr.ontimeout = () => resolve({ ok: false, problem: NETWORK_PROBLEM });
    xhr.onabort = () => resolve({ ok: false, problem: NETWORK_PROBLEM });
    xhr.onload = () => {
      if (xhr.status === 202 || xhr.status === 200) {
        try {
          const body = JSON.parse(xhr.responseText) as UploadedDocument;
          if (typeof body.id === "string") return resolve({ ok: true, document: body });
        } catch {
          // fall through
        }
        return resolve({
          ok: false,
          problem: { status: 502, title: "Unexpected response from the service" },
        });
      }
      resolve({
        ok: false,
        problem: problemFromBody(
          xhr.status,
          xhr.responseText,
          xhr.getResponseHeader("Retry-After"),
        ),
      });
    };
    signal?.addEventListener("abort", () => xhr.abort(), { once: true });
    const form = new FormData();
    form.append("file", file, file.name);
    xhr.send(form);
  });
}

/** Ticket, then the file. The file's bytes are only ever held by the File object and the request. */
export async function uploadFile(
  file: File,
  onProgress: (fraction: number) => void,
  signal?: AbortSignal,
): Promise<UploadOutcome> {
  const ticket = await requestTicket(signal);
  if (!ticket.ok) return ticket;
  return postFile(file, ticket.ticket, onProgress, signal);
}
