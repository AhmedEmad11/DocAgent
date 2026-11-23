import { afterEach, describe, expect, it, vi } from "vitest";
import { postFile, requestTicket, uploadFile } from "./upload-client";

afterEach(() => vi.unstubAllGlobals());

class FakeXhr {
  static last: FakeXhr;
  headers: Record<string, string> = {};
  method = "";
  url = "";
  body: FormData | null = null;
  upload: {
    onprogress?: (e: { lengthComputable: boolean; loaded: number; total: number }) => void;
  } = {};
  status = 0;
  responseText = "";
  onload?: () => void;
  onerror?: () => void;
  ontimeout?: () => void;
  onabort?: () => void;
  responseHeaders: Record<string, string> = {};
  constructor() {
    FakeXhr.last = this;
  }
  open(method: string, url: string) {
    this.method = method;
    this.url = url;
  }
  setRequestHeader(k: string, v: string) {
    this.headers[k] = v;
  }
  getResponseHeader(k: string) {
    return this.responseHeaders[k] ?? null;
  }
  send(body: FormData) {
    this.body = body;
  }
  abort() {
    this.onabort?.();
  }
}

const ticket = {
  ticket: "T-ONE-TIME",
  uploadUrl: "https://api.example/api/v1/documents",
  expiresInSeconds: 60,
};
const file = new File(["hello"], "a.txt", { type: "text/plain" });

describe("upload client (plan A3)", () => {
  it("posts the file straight to the API with only the ticket, never an Authorization header", async () => {
    vi.stubGlobal("XMLHttpRequest", FakeXhr);
    const progress: number[] = [];
    const promise = postFile(file, ticket, (f) => progress.push(f));
    const xhr = FakeXhr.last;
    xhr.upload.onprogress?.({ lengthComputable: true, loaded: 5, total: 10 });
    xhr.status = 202;
    xhr.responseText = JSON.stringify({ id: "i", fileName: "a.txt", status: "Pending" });
    xhr.onload?.();

    expect(await promise).toEqual({
      ok: true,
      document: { id: "i", fileName: "a.txt", status: "Pending" },
    });
    expect(xhr.method).toBe("POST");
    expect(xhr.url).toBe(ticket.uploadUrl);
    expect(xhr.headers["X-Upload-Ticket"]).toBe("T-ONE-TIME");
    expect(Object.keys(xhr.headers).map((h) => h.toLowerCase())).not.toContain("authorization");
    expect((xhr.body as FormData).get("file")).toBeInstanceOf(File);
    expect(progress).toEqual([0.5]);
  });

  it("maps API refusals (413, 415, 409, 429 with Retry-After) to problems", async () => {
    vi.stubGlobal("XMLHttpRequest", FakeXhr);
    for (const [status, retry] of [
      [413, null],
      [415, null],
      [409, null],
      [429, "3"],
    ] as const) {
      const promise = postFile(file, ticket, () => {});
      const xhr = FakeXhr.last;
      xhr.status = status;
      xhr.responseText = JSON.stringify({ title: "x", detail: "y" });
      if (retry) xhr.responseHeaders["Retry-After"] = retry;
      xhr.onload?.();
      const result = await promise;
      expect(result.ok).toBe(false);
      if (!result.ok) {
        expect(result.problem.status).toBe(status);
        expect(result.problem.retryAfter).toBe(retry ? 3 : undefined);
      }
    }
  });

  it("reports a network failure and an unreadable success body", async () => {
    vi.stubGlobal("XMLHttpRequest", FakeXhr);
    let promise = postFile(file, ticket, () => {});
    FakeXhr.last.onerror?.();
    const failed = await promise;
    expect(!failed.ok && failed.problem.code).toBe("network");

    promise = postFile(file, ticket, () => {});
    FakeXhr.last.status = 202;
    FakeXhr.last.responseText = "not json";
    FakeXhr.last.onload?.();
    const bad = await promise;
    expect(!bad.ok && bad.problem.status).toBe(502);
  });

  it("asks the BFF for the ticket first and stops there when it is refused", async () => {
    const fetchMock = vi.fn(
      async () =>
        new Response(JSON.stringify({ title: "Unauthorized", code: "session_ended" }), {
          status: 401,
        }),
    );
    vi.stubGlobal("fetch", fetchMock);
    const result = await uploadFile(file, () => {});
    expect(fetchMock).toHaveBeenCalledWith(
      "/api/upload/ticket",
      expect.objectContaining({ method: "POST" }),
    );
    expect(!result.ok && result.problem.code).toBe("session_ended");
  });

  it("rejects a ticket response without the fields it needs", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn(async () => new Response("{}", { status: 200 })),
    );
    const t = await requestTicket();
    expect(!t.ok && t.problem.status).toBe(502);
  });
});
