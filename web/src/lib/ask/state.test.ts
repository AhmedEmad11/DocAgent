import { describe, expect, it } from "vitest";
import { DROPPED_PROBLEM, askReducer, initialAskState, type AskState } from "./state";
import type { Answer } from "./events";

const answer: Answer = {
  answer: "x",
  notFound: false,
  citations: [],
  mode: "demo",
  provider: "p",
  model: "m",
  incomplete: false,
  notices: [],
};
const run = (s: AskState, ...actions: Parameters<typeof askReducer>[1][]) =>
  actions.reduce(askReducer, s);
const started = askReducer(initialAskState, { type: "start" });

describe("ask state machine", () => {
  it("moves through the stages and ends with the answer", () => {
    expect(started).toEqual({ phase: "running", stage: "starting" });
    expect(run(started, { type: "event", event: { type: "searching" } })).toEqual({
      phase: "running",
      stage: "searching",
    });
    expect(
      run(
        started,
        { type: "event", event: { type: "searching" } },
        { type: "event", event: { type: "tool_call" } },
      ),
    ).toEqual({ phase: "running", stage: "checking" });
    const done = run(
      started,
      { type: "event", event: { type: "verifying" } },
      { type: "event", event: { type: "answer", answer } },
    );
    expect(done).toEqual({ phase: "answered", answer });
  });

  it("never moves backwards (a tool call after verifying keeps verifying)", () => {
    const s = run(
      started,
      { type: "event", event: { type: "verifying" } },
      { type: "event", event: { type: "tool_call" } },
    );
    expect(s).toEqual({ phase: "running", stage: "verifying" });
  });

  it("an error event mid-stream fails with its status and code", () => {
    const s = run(
      started,
      { type: "event", event: { type: "searching" } },
      {
        type: "event",
        event: { type: "error", status: 429, title: "t", code: "demo_rate_limited", retryAfter: 5 },
      },
    );
    expect(s).toEqual({
      phase: "failed",
      problem: { status: 429, title: "t", code: "demo_rate_limited", retryAfter: 5 },
    });
  });

  it("a refusal before the stream and a dropped connection fail; a drop is the network problem", () => {
    expect(run(started, { type: "refused", problem: { status: 409, title: "c" } })).toEqual({
      phase: "failed",
      problem: { status: 409, title: "c" },
    });
    expect(run(started, { type: "dropped" })).toEqual({
      phase: "failed",
      problem: DROPPED_PROBLEM,
    });
  });

  it("cancel wins: late events after a cancel are ignored", () => {
    const s = run(
      started,
      { type: "cancel" },
      { type: "event", event: { type: "answer", answer } },
      { type: "dropped" },
    );
    expect(s).toEqual({ phase: "cancelled" });
  });

  it("cancel, refusals and events do nothing outside a running question", () => {
    expect(askReducer(initialAskState, { type: "cancel" })).toBe(initialAskState);
    const done: AskState = { phase: "answered", answer };
    expect(askReducer(done, { type: "dropped" })).toBe(done);
  });

  it("start restarts a failed or cancelled question (retry)", () => {
    expect(run({ phase: "failed", problem: DROPPED_PROBLEM }, { type: "start" })).toEqual({
      phase: "running",
      stage: "starting",
    });
  });
});
