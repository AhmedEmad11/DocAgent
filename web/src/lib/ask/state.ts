import type { ApiProblem } from "@/lib/api/problem";
import type { Answer, AskEvent } from "./events";

// State machine of one question (UI-QA-2, 7, 8). Stages are generic on purpose (OI-11): the API's tool names are internal.
export type AskStage = "starting" | "searching" | "checking" | "verifying";

export type AskState =
  | { phase: "idle" }
  | { phase: "running"; stage: AskStage }
  | { phase: "answered"; answer: Answer }
  | { phase: "failed"; problem: ApiProblem }
  | { phase: "cancelled" };

export type AskAction =
  | { type: "start" }
  | { type: "event"; event: AskEvent }
  | { type: "refused"; problem: ApiProblem }
  | { type: "dropped" }
  | { type: "cancel" };

export const initialAskState: AskState = { phase: "idle" };

/** The connection ended without an answer or an error event (network drop, proxy cut). */
export const DROPPED_PROBLEM: ApiProblem = { status: 0, title: "Connection lost", code: "network" };

const ORDER: Record<AskStage, number> = { starting: 0, searching: 1, checking: 2, verifying: 3 };

export function askReducer(state: AskState, action: AskAction): AskState {
  switch (action.type) {
    case "start":
      return { phase: "running", stage: "starting" };
    case "cancel":
      return state.phase === "running" ? { phase: "cancelled" } : state;
    case "refused":
      return state.phase === "running" ? { phase: "failed", problem: action.problem } : state;
    case "dropped":
      return state.phase === "running" ? { phase: "failed", problem: DROPPED_PROBLEM } : state;
    case "event": {
      if (state.phase !== "running") return state; // late events after a cancel are ignored
      const e = action.event;
      switch (e.type) {
        case "answer":
          return { phase: "answered", answer: e.answer };
        case "error":
          return {
            phase: "failed",
            problem: {
              status: e.status,
              title: e.title,
              ...(e.code ? { code: e.code } : {}),
              ...(e.retryAfter ? { retryAfter: e.retryAfter } : {}),
            },
          };
        default: {
          const next: AskStage =
            e.type === "searching"
              ? "searching"
              : e.type === "tool_call"
                ? "checking"
                : "verifying";
          // Stages only move forward: a second tool_call after verifying (retry loop) does not go backwards.
          return ORDER[next] >= ORDER[state.stage] ? { phase: "running", stage: next } : state;
        }
      }
    }
  }
}
