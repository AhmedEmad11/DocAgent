"use client";

import { useEffect, useRef } from "react";
import { documentListSchema, type ApiDocument } from "@/lib/api/documents-schema";
import { bffFetch } from "@/lib/client/bff";
import {
  MIN_POLL_MS,
  hasActive,
  pollDelayMs,
  statusChanges,
  type StatusChange,
} from "@/lib/documents/poll-schedule";

export type ListFetcher = () => Promise<{ ok: true; documents: ApiDocument[] } | { ok: false }>;

export const fetchDocumentList: ListFetcher = async () => {
  const res = await bffFetch("/api/proxy/documents", {}, (b) => documentListSchema.parse(b));
  return res.ok ? { ok: true, documents: res.data } : { ok: false };
};

interface Options {
  documents: ApiDocument[];
  /** Receives the fresh list, minus anything deleted meanwhile. */
  onList: (docs: ApiDocument[], changes: StatusChange[]) => void;
  onError: (failed: boolean) => void;
  fetchList?: ListFetcher;
  /** Ids deleted by the user; a poll that was already in flight must not bring them back. */
  removed: React.RefObject<Set<string>>;
}

/**
 * Polls while any document is Pending or Processing (UI-DOC-4): 2 s, backing off to 5 s while nothing changes, never more than
 * one request per 2 s, nothing while the tab is hidden, and a refresh as soon as it is visible again. Stops when all are final.
 */
export function useDocumentPolling({
  documents,
  onList,
  onError,
  fetchList = fetchDocumentList,
  removed,
}: Options) {
  const active = hasActive(documents);
  const latest = useRef({ documents, onList, onError, fetchList });
  useEffect(() => {
    latest.current = { documents, onList, onError, fetchList };
  });

  useEffect(() => {
    if (!active) return;
    let stopped = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    let unchanged = 0;
    let lastRequest = 0;
    let inFlight = false;

    const schedule = (delay: number) => {
      clearTimeout(timer);
      if (!stopped && !document.hidden) timer = setTimeout(tick, delay);
    };

    async function tick() {
      if (stopped || document.hidden || inFlight) return;
      inFlight = true;
      lastRequest = Date.now();
      const result = await latest.current.fetchList();
      inFlight = false;
      if (stopped) return;
      if (result.ok) {
        const fresh = result.documents.filter((d) => !removed.current.has(d.id));
        const changes = statusChanges(latest.current.documents, fresh);
        unchanged = changes.length > 0 ? 0 : unchanged + 1;
        latest.current.onError(false);
        latest.current.onList(fresh, changes);
        // When everything is final the effect is torn down by the state update.
      } else {
        unchanged += 1;
        latest.current.onError(true);
      }
      schedule(pollDelayMs(unchanged));
    }

    const onVisibility = () => {
      if (document.hidden) {
        clearTimeout(timer);
      } else {
        schedule(Math.max(0, MIN_POLL_MS - (Date.now() - lastRequest)));
      }
    };
    document.addEventListener("visibilitychange", onVisibility);
    schedule(pollDelayMs(0));
    return () => {
      stopped = true;
      clearTimeout(timer);
      document.removeEventListener("visibilitychange", onVisibility);
    };
  }, [active, removed]);
}
