"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import type { ApiProblem } from "@/lib/api/problem";
import { checkFile, type TierLimits } from "@/lib/documents/limits";
import type { UploadOutcome, UploadedDocument } from "@/lib/documents/upload-client";

export type UploadItemState = "queued" | "uploading" | "done" | "failed";
export interface UploadItem {
  id: number;
  name: string;
  size: number;
  state: UploadItemState;
  progress: number;
  /** Local refusal before sending (UI-DOC-2). */
  localReason?: "unsupported" | "tooLarge" | "empty" | "documentLimit";
  /** The API's or the BFF's refusal. */
  problem?: ApiProblem;
}

type UploadFn = (
  file: File,
  onProgress: (fraction: number) => void,
  signal?: AbortSignal,
) => Promise<UploadOutcome>;

interface Options {
  limits: TierLimits;
  /** Documents the account holds now (all statuses count towards the limit). */
  documentCount: number;
  upload: UploadFn;
  onUploaded: (doc: UploadedDocument) => void;
}

/**
 * Upload queue (UI-DOC-3): files go one after the other, each fails on its own and does not stop the rest.
 * Files that fail the local checks are listed as failed without being sent.
 */
export function useUploader({ limits, documentCount, upload, onUploaded }: Options) {
  const [items, setItems] = useState<UploadItem[]>([]);
  const nextId = useRef(1);
  const queue = useRef<{ id: number; file: File }[]>([]);
  const running = useRef(false);
  const count = useRef(documentCount);
  const aborter = useRef<AbortController | null>(null);
  const latest = useRef({ upload, onUploaded });

  useEffect(() => {
    count.current = documentCount;
    latest.current = { upload, onUploaded };
  });

  useEffect(() => () => aborter.current?.abort(), []);

  const patch = useCallback((id: number, change: Partial<UploadItem>) => {
    setItems((all) => all.map((it) => (it.id === id ? { ...it, ...change } : it)));
  }, []);

  const drain = useCallback(async () => {
    if (running.current) return;
    running.current = true;
    try {
      for (let job = queue.current.shift(); job; job = queue.current.shift()) {
        patch(job.id, { state: "uploading", progress: 0 });
        aborter.current = new AbortController();
        const outcome = await latest.current.upload(
          job.file,
          (progress) => patch(job.id, { progress }),
          aborter.current.signal,
        );
        if (outcome.ok) {
          count.current += 1;
          patch(job.id, { state: "done", progress: 1 });
          latest.current.onUploaded(outcome.document);
        } else {
          patch(job.id, { state: "failed", problem: outcome.problem });
        }
      }
    } finally {
      running.current = false;
    }
  }, [patch]);

  const addFiles = useCallback(
    (files: File[]) => {
      let reserved = count.current + queue.current.length;
      const added: UploadItem[] = [];
      for (const file of files) {
        const id = nextId.current++;
        const check = checkFile(file, limits, limits.maxDocuments - reserved);
        const base = { id, name: file.name, size: file.size, progress: 0 };
        if (check.ok) {
          reserved += 1;
          queue.current.push({ id, file });
          added.push({ ...base, state: "queued" });
        } else {
          added.push({ ...base, state: "failed", localReason: check.reason });
        }
      }
      setItems((all) => [...added, ...all]);
      void drain();
    },
    [drain, limits],
  );

  const dismiss = useCallback(
    (id: number) => setItems((all) => all.filter((it) => it.id !== id)),
    [],
  );
  const clearFinished = useCallback(
    () => setItems((all) => all.filter((it) => it.state === "queued" || it.state === "uploading")),
    [],
  );

  return { items, addFiles, dismiss, clearFinished };
}
