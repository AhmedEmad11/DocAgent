import { describe, expect, it } from "vitest";
import type { ApiDocument } from "@/lib/api/documents-schema";
import { MAX_POLL_MS, MIN_POLL_MS, hasActive, pollDelayMs, statusChanges } from "./poll-schedule";

const doc = (id: string, status: ApiDocument["status"]): ApiDocument => ({
  id,
  fileName: `${id}.pdf`,
  contentType: "application/pdf",
  sizeBytes: 1,
  pageCount: 1,
  chunkCount: 0,
  status,
  failureReason: null,
  extractionNotice: null,
  createdAt: "2026-11-23T10:00:00Z",
});

describe("poll schedule (UI-DOC-4, UI-PERF-5)", () => {
  it("starts at 2 s, backs off while nothing changes and never exceeds 5 s", () => {
    expect(pollDelayMs(0)).toBe(2000);
    expect(pollDelayMs(1)).toBeGreaterThan(pollDelayMs(0));
    expect(pollDelayMs(100)).toBe(MAX_POLL_MS);
    expect(MAX_POLL_MS).toBe(5000);
  });

  it("is never faster than one request per 2 s", () => {
    for (let n = -3; n < 20; n++) expect(pollDelayMs(n)).toBeGreaterThanOrEqual(MIN_POLL_MS);
  });

  it("polls only while a document is Pending or Processing", () => {
    expect(hasActive([doc("a", "Ready"), doc("b", "Failed")])).toBe(false);
    expect(hasActive([doc("a", "Ready"), doc("b", "Pending")])).toBe(true);
    expect(hasActive([doc("a", "Processing")])).toBe(true);
    expect(hasActive([])).toBe(false);
  });

  it("reports status changes, not unchanged polls", () => {
    const before = [doc("a", "Pending"), doc("b", "Ready")];
    expect(statusChanges(before, before)).toEqual([]);
    const after = [doc("a", "Processing"), doc("b", "Ready")];
    expect(statusChanges(before, after)).toEqual([
      { id: "a", fileName: "a.pdf", from: "Pending", to: "Processing" },
    ]);
  });

  it("does not announce a brand new active document, but does announce one that appears final", () => {
    expect(statusChanges([], [doc("n", "Pending")])).toEqual([]);
    expect(statusChanges([], [doc("n", "Ready")])).toHaveLength(1);
  });
});
