import { act, render } from "@testing-library/react";
import { useRef } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { ApiDocument } from "@/lib/api/documents-schema";
import { useDocumentPolling, type ListFetcher } from "./use-document-polling";

const doc = (id: string, status: ApiDocument["status"]): ApiDocument => ({
  id,
  fileName: `${id}.txt`,
  contentType: "text/plain",
  sizeBytes: 1,
  pageCount: null,
  chunkCount: 0,
  status,
  failureReason: null,
  extractionNotice: null,
  createdAt: "2026-11-23T10:00:00Z",
});

function Harness({
  documents,
  fetchList,
  onList,
  onError = () => {},
  removed,
}: {
  documents: ApiDocument[];
  fetchList: ListFetcher;
  onList: (d: ApiDocument[]) => void;
  onError?: (f: boolean) => void;
  removed?: Set<string>;
}) {
  const ref = useRef(removed ?? new Set<string>());
  useDocumentPolling({ documents, fetchList, onList, onError, removed: ref });
  return null;
}

function setHidden(hidden: boolean) {
  Object.defineProperty(document, "hidden", { configurable: true, get: () => hidden });
  document.dispatchEvent(new Event("visibilitychange"));
}

beforeEach(() => vi.useFakeTimers());
afterEach(() => {
  setHidden(false);
  vi.useRealTimers();
});

describe("useDocumentPolling (UI-DOC-4, UI-PERF-5)", () => {
  it("polls every 2 s while a document is active, then stops when all are final", async () => {
    const fetchList = vi
      .fn<ListFetcher>()
      .mockResolvedValue({ ok: true, documents: [doc("a", "Ready")] });
    const onList = vi.fn();
    const { rerender } = render(
      <Harness documents={[doc("a", "Pending")]} fetchList={fetchList} onList={onList} />,
    );

    await act(async () => void (await vi.advanceTimersByTimeAsync(1999)));
    expect(fetchList).not.toHaveBeenCalled(); // never faster than 2 s
    await act(async () => void (await vi.advanceTimersByTimeAsync(2)));
    expect(fetchList).toHaveBeenCalledTimes(1);
    expect(onList).toHaveBeenCalledWith([doc("a", "Ready")], expect.any(Array));

    rerender(<Harness documents={[doc("a", "Ready")]} fetchList={fetchList} onList={onList} />);
    await act(async () => void (await vi.advanceTimersByTimeAsync(20000)));
    expect(fetchList).toHaveBeenCalledTimes(1); // all final: no more requests
  });

  it("backs off towards 5 s while nothing changes", async () => {
    const fetchList = vi
      .fn<ListFetcher>()
      .mockResolvedValue({ ok: true, documents: [doc("a", "Processing")] });
    render(
      <Harness documents={[doc("a", "Processing")]} fetchList={fetchList} onList={() => {}} />,
    );
    await act(async () => void (await vi.advanceTimersByTimeAsync(60_000)));
    // 2.0, 2.5, 3.0, 3.5, 4.0, 4.5, then 5 s each: well under one request per 2 s (30 would be the maximum in a minute).
    expect(fetchList.mock.calls.length).toBeLessThanOrEqual(15);
    expect(fetchList.mock.calls.length).toBeGreaterThanOrEqual(11);
  });

  it("does not request while the tab is hidden and refreshes when it is visible again", async () => {
    const fetchList = vi
      .fn<ListFetcher>()
      .mockResolvedValue({ ok: true, documents: [doc("a", "Processing")] });
    render(
      <Harness documents={[doc("a", "Processing")]} fetchList={fetchList} onList={() => {}} />,
    );
    await act(async () => setHidden(true));
    await act(async () => void (await vi.advanceTimersByTimeAsync(30_000)));
    expect(fetchList).not.toHaveBeenCalled();
    await act(async () => setHidden(false));
    await act(async () => void (await vi.advanceTimersByTimeAsync(2100)));
    expect(fetchList).toHaveBeenCalledTimes(1);
  });

  it("reports a failed refresh, keeps trying, and clears the flag on success", async () => {
    const fetchList = vi
      .fn<ListFetcher>()
      .mockResolvedValueOnce({ ok: false })
      .mockResolvedValue({ ok: true, documents: [doc("a", "Processing")] });
    const onError = vi.fn();
    render(
      <Harness
        documents={[doc("a", "Processing")]}
        fetchList={fetchList}
        onList={() => {}}
        onError={onError}
      />,
    );
    await act(async () => void (await vi.advanceTimersByTimeAsync(2100)));
    expect(onError).toHaveBeenLastCalledWith(true);
    await act(async () => void (await vi.advanceTimersByTimeAsync(3000)));
    expect(onError).toHaveBeenLastCalledWith(false);
  });

  it("drops documents the user deleted while a poll was in flight", async () => {
    const fetchList = vi.fn<ListFetcher>().mockResolvedValue({
      ok: true,
      documents: [doc("a", "Processing"), doc("gone", "Ready")],
    });
    const onList = vi.fn();
    render(
      <Harness
        documents={[doc("a", "Processing")]}
        fetchList={fetchList}
        onList={onList}
        removed={new Set(["gone"])}
      />,
    );
    await act(async () => void (await vi.advanceTimersByTimeAsync(2100)));
    expect(onList.mock.calls[0]![0].map((d: ApiDocument) => d.id)).toEqual(["a"]);
  });
});
