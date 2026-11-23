import { describe, expect, it } from "vitest";
import { createSseParser, type SseMessage } from "./parser";

function collect(feed: (push: (s: string) => void, end: () => void) => void): SseMessage[] {
  const out: SseMessage[] = [];
  const p = createSseParser((m) => out.push(m));
  feed(p.push, p.end);
  return out;
}

const STREAM =
  'event: searching\ndata: {}\n\nevent: tool_call\ndata: {"tool":"search"}\n\nevent: verifying\ndata: {}\n\nevent: answer\ndata: {"answer":"x"}\n\n';

describe("SSE parser", () => {
  it("parses the API's stream into messages", () => {
    const out = collect((push) => push(STREAM));
    expect(out.map((m) => m.event)).toEqual(["searching", "tool_call", "verifying", "answer"]);
    expect(out[1]!.data).toBe('{"tool":"search"}');
  });

  it("gives the same result however the text is split, even one character at a time", () => {
    const whole = collect((push) => push(STREAM));
    const bytewise = collect((push) => [...STREAM].forEach((c) => push(c)));
    expect(bytewise).toEqual(whole);
    for (const size of [2, 3, 7, 13, 40]) {
      const chunks = collect((push) => {
        for (let i = 0; i < STREAM.length; i += size) push(STREAM.slice(i, i + size));
      });
      expect(chunks).toEqual(whole);
    }
  });

  it("handles CRLF and CR line ends, also split between chunks", () => {
    const crlf = "event: a\r\ndata: 1\r\n\r\nevent: b\r\ndata: 2\r\n\r\n";
    expect(collect((push) => push(crlf)).map((m) => m.data)).toEqual(["1", "2"]);
    expect(collect((push) => [...crlf].forEach((c) => push(c))).map((m) => m.data)).toEqual([
      "1",
      "2",
    ]);
    expect(collect((push) => push("event: a\rdata: 1\r\r")).map((m) => m.data)).toEqual(["1"]);
  });

  it("joins multi-line data with a newline and strips one leading space only", () => {
    const out = collect((push) => push("event: x\ndata: a\ndata:  b\ndata:c\n\n"));
    expect(out[0]!.data).toBe("a\n b\nc");
  });

  it("ignores comments, id, retry and unknown fields; defaults the event name", () => {
    const out = collect((push) => push(": keep-alive\nid: 7\nretry: 100\nfoo: bar\ndata: hi\n\n"));
    expect(out).toEqual([{ event: "message", data: "hi" }]);
  });

  it("emits nothing for a block without data, and nothing for an unfinished block until end()", () => {
    expect(collect((push) => push("event: only\n\n"))).toEqual([]);
    const out = collect((push, end) => {
      push("event: answer\ndata: {}");
      expect(out_len()).toBe(0);
      end();
    });
    function out_len() {
      return 0;
    }
    expect(out).toEqual([{ event: "answer", data: "{}" }]);
  });

  it("keeps UTF-8 text such as Arabic intact across chunk borders", () => {
    const text = 'event: answer\ndata: {"answer":"مرحبا بالعالم"}\n\n';
    const out = collect((push) => [...text].forEach((c) => push(c)));
    expect(JSON.parse(out[0]!.data).answer).toBe("مرحبا بالعالم");
  });
});
