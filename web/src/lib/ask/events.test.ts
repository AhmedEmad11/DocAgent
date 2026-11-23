import { describe, expect, it } from "vitest";
import { parseAskEvent } from "./events";

const answer = {
  answer: "A [1]",
  notFound: false,
  citations: [
    { index: 1, documentId: "d", fileName: "f.pdf", pageNumber: 2, section: null, quote: "q" },
  ],
  mode: "byok",
  provider: "openrouter",
  model: "m",
  incomplete: false,
  toolCalls: 2,
  notices: [],
};

describe("parseAskEvent", () => {
  it("maps stage events and drops tool names (OI-11)", () => {
    expect(parseAskEvent("searching", "{}")).toEqual({ type: "searching" });
    expect(parseAskEvent("verifying", "{}")).toEqual({ type: "verifying" });
    expect(parseAskEvent("tool_call", '{"tool":"search_documents"}')).toEqual({
      type: "tool_call",
    });
  });

  it("validates the answer", () => {
    const e = parseAskEvent("answer", JSON.stringify(answer));
    expect(e?.type).toBe("answer");
    expect(parseAskEvent("answer", JSON.stringify({ ...answer, citations: "nope" }))).toBeNull();
    expect(parseAskEvent("answer", "not json")).toBeNull();
  });

  it("reads the error event with code and retry hint", () => {
    expect(
      parseAskEvent(
        "error",
        '{"status":429,"title":"Busy","code":"demo_rate_limited","retryAfter":5}',
      ),
    ).toEqual({
      type: "error",
      status: 429,
      title: "Busy",
      code: "demo_rate_limited",
      retryAfter: 5,
    });
    expect(
      parseAskEvent("error", '{"status":504,"title":"Timed out","code":null,"retryAfter":null}'),
    ).toEqual({
      type: "error",
      status: 504,
      title: "Timed out",
    });
  });

  it("turns an unreadable error event into a generic 500 and ignores unknown events", () => {
    expect(parseAskEvent("error", "garbage")).toMatchObject({ type: "error", status: 500 });
    expect(parseAskEvent("ping", "{}")).toBeNull();
  });
});
