import { act, renderHook, waitFor } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { ApiProblem } from "@/lib/api/problem";
import { DEMO_LIMITS } from "@/lib/documents/limits";
import type { UploadOutcome } from "@/lib/documents/upload-client";
import { useUploader } from "./use-uploader";

const file = (name: string, size = 100) => new File([new Uint8Array(size)], name);
const okOutcome = (name: string): UploadOutcome => ({
  ok: true,
  document: { id: `id-${name}`, fileName: name, status: "Pending" },
});
const problem: ApiProblem = { status: 415, title: "Unsupported" };

describe("useUploader (UI-DOC-3)", () => {
  it("uploads files one after another and reports each", async () => {
    const order: string[] = [];
    const upload = vi.fn(async (f: File) => {
      order.push(`start ${f.name}`);
      await Promise.resolve();
      order.push(`end ${f.name}`);
      return okOutcome(f.name);
    });
    const onUploaded = vi.fn();
    const { result } = renderHook(() =>
      useUploader({ limits: DEMO_LIMITS, documentCount: 0, upload, onUploaded }),
    );

    act(() => result.current.addFiles([file("a.txt"), file("b.md")]));
    await waitFor(() => expect(result.current.items.every((i) => i.state === "done")).toBe(true));

    expect(order).toEqual(["start a.txt", "end a.txt", "start b.md", "end b.md"]);
    expect(onUploaded).toHaveBeenCalledTimes(2);
    expect(onUploaded.mock.calls[0]![0].fileName).toBe("a.txt");
  });

  it("a failing file does not stop the others", async () => {
    const upload = vi.fn(async (f: File) =>
      f.name === "bad.txt" ? { ok: false as const, problem } : okOutcome(f.name),
    );
    const { result } = renderHook(() =>
      useUploader({ limits: DEMO_LIMITS, documentCount: 0, upload, onUploaded: vi.fn() }),
    );
    act(() => result.current.addFiles([file("bad.txt"), file("good.txt")]));
    await waitFor(() =>
      expect(
        result.current.items.every((i) => i.state !== "queued" && i.state !== "uploading"),
      ).toBe(true),
    );
    const byName = Object.fromEntries(result.current.items.map((i) => [i.name, i]));
    expect(byName["bad.txt"]!.state).toBe("failed");
    expect(byName["bad.txt"]!.problem).toEqual(problem);
    expect(byName["good.txt"]!.state).toBe("done");
  });

  it("refuses wrong types, empty and oversized files locally without calling the API", async () => {
    const upload = vi.fn(async (f: File) => okOutcome(f.name));
    const { result } = renderHook(() =>
      useUploader({ limits: DEMO_LIMITS, documentCount: 0, upload, onUploaded: vi.fn() }),
    );
    act(() =>
      result.current.addFiles([
        file("a.exe"),
        file("empty.txt", 0),
        file("big.pdf", 5 * 1024 * 1024 + 1),
      ]),
    );
    expect(upload).not.toHaveBeenCalled();
    const reasons = Object.fromEntries(result.current.items.map((i) => [i.name, i.localReason]));
    expect(reasons).toEqual({
      "a.exe": "unsupported",
      "empty.txt": "empty",
      "big.pdf": "tooLarge",
    });
  });

  it("stops queueing at the document limit, counting the files already waiting", async () => {
    const upload = vi.fn(async (f: File) => okOutcome(f.name));
    const { result } = renderHook(() =>
      useUploader({ limits: DEMO_LIMITS, documentCount: 1, upload, onUploaded: vi.fn() }),
    );
    act(() => result.current.addFiles([file("1.txt"), file("2.txt"), file("3.txt")]));
    await waitFor(() => expect(upload).toHaveBeenCalledTimes(2));
    expect(result.current.items.find((i) => i.name === "3.txt")!.localReason).toBe("documentLimit");
  });

  it("reports progress and can dismiss or clear finished items", async () => {
    let release!: () => void;
    const upload = vi.fn(
      (f: File, onProgress: (n: number) => void) =>
        new Promise<UploadOutcome>((resolve) => {
          onProgress(0.4);
          release = () => resolve(okOutcome(f.name));
        }),
    );
    const { result } = renderHook(() =>
      useUploader({ limits: DEMO_LIMITS, documentCount: 0, upload, onUploaded: vi.fn() }),
    );
    act(() => result.current.addFiles([file("a.txt")]));
    await waitFor(() => expect(result.current.items[0]!.progress).toBe(0.4));
    expect(result.current.items[0]!.state).toBe("uploading");
    await act(async () => release());
    await waitFor(() => expect(result.current.items[0]!.state).toBe("done"));
    act(() => result.current.clearFinished());
    expect(result.current.items).toHaveLength(0);
  });
});
