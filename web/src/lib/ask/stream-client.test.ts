import { afterEach, describe, expect, it, vi } from "vitest";
import type { AskEvent } from "./events";
import { askStream } from "./stream-client";

afterEach(() => vi.unstubAllGlobals());

const enc = new TextEncoder();
const answerJson = JSON.stringify({
  answer: "A",
  notFound: false,
  citations: [],
  mode: "byok",
  provider: "p",
  model: "m",
  incomplete: false,
  notices: [],
});

function streamOf(parts: (string | Error)[]): Response {
  let i = 0;
  const body = new ReadableStream<Uint8Array>({
    pull(controller) {
      const part = parts[i++];
      if (part === undefined) return controller.close();
      if (part instanceof Error) return controller.error(part);
      controller.enqueue(enc.encode(part));
    },
  });
  return new Response(body, { status: 200, headers: { "content-type": "text/event-stream" } });
}

const run = async (
  response: Response | Error | DOMException,
  signal = new AbortController().signal,
) => {
  vi.stubGlobal(
    "fetch",
    vi.fn(async () => {
      if (!(response instanceof Response)) throw response;
      return response;
    }),
  );
  const events: AskEvent[] = [];
  const end = await askStream({ question: "q" }, (e) => events.push(e), signal);
  return { end, events };
};

describe("askStream", () => {
  it("delivers events as they arrive, including when chunks split an event, and finishes on the answer", async () => {
    const { end, events } = await run(
      streamOf([
        "event: searching\nda",
        "ta: {}\n\nevent: verif",
        `ying\ndata: {}\n\nevent: answer\ndata: ${answerJson}\n\n`,
      ]),
    );
    expect(end).toEqual({ kind: "finished" });
    expect(events.map((e) => e.type)).toEqual(["searching", "verifying", "answer"]);
  });

  it("finishes on an error event", async () => {
    const { end, events } = await run(
      streamOf([
        'event: searching\ndata: {}\n\nevent: error\ndata: {"status":504,"title":"Timed out"}\n\n',
      ]),
    );
    expect(end).toEqual({ kind: "finished" });
    expect(events.at(-1)).toEqual({ type: "error", status: 504, title: "Timed out" });
  });

  it("reports a dropped connection when the stream ends without an answer or error", async () => {
    const { end, events } = await run(streamOf(["event: searching\ndata: {}\n\n"]));
    expect(end).toEqual({ kind: "dropped" });
    expect(events).toHaveLength(1);
    const broken = await run(streamOf(["event: searching\ndata: {}\n\n", new Error("reset")]));
    expect(broken.end).toEqual({ kind: "dropped" });
  });

  it("ignores events after the answer", async () => {
    const { events } = await run(
      streamOf([
        `event: answer\ndata: ${answerJson}\n\nevent: error\ndata: {"status":500,"title":"x"}\n\n`,
      ]),
    );
    expect(events.map((e) => e.type)).toEqual(["answer"]);
  });

  it("returns the Problem Details of a refusal before the stream", async () => {
    const res = new Response(
      JSON.stringify({ title: "Quota", code: "demo_quota_exceeded", status: 429 }),
      { status: 429, headers: { "retry-after": "30" } },
    );
    const { end } = await run(res);
    expect(end).toMatchObject({
      kind: "refused",
      problem: { status: 429, code: "demo_quota_exceeded", retryAfter: 30 },
    });
  });

  it("reports the network problem when the request cannot be made, and aborted when cancelled", async () => {
    expect(await run(new TypeError("offline"))).toMatchObject({
      end: { kind: "refused", problem: { code: "network" } },
    });
    const c = new AbortController();
    c.abort();
    expect((await run(new DOMException("x", "AbortError"), c.signal)).end).toEqual({
      kind: "aborted",
    });
  });

  it("posts JSON to our own relay, never to the API", async () => {
    const fetchMock = vi.fn(async () => streamOf([`event: answer\ndata: ${answerJson}\n\n`]));
    vi.stubGlobal("fetch", fetchMock);
    await askStream(
      { question: "Why?", documentIds: ["a"] },
      () => {},
      new AbortController().signal,
    );
    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe("/api/ask/stream");
    expect(init.method).toBe("POST");
    expect(JSON.parse(init.body as string)).toEqual({ question: "Why?", documentIds: ["a"] });
  });
});
