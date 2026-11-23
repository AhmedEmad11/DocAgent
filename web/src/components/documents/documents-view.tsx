"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { useLocale, useTranslations } from "next-intl";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { ProblemAlert } from "@/components/ui/problem-alert";
import { UsageMeter } from "@/components/ui/usage-meter";
import { dynamicT } from "@/i18n/dynamic";
import type { ApiDocument } from "@/lib/api/documents-schema";
import type { ApiProblem } from "@/lib/api/problem";
import { bffFetch } from "@/lib/client/bff";
import { formatBytes } from "@/lib/documents/format";
import { limitsFor } from "@/lib/documents/limits";
import type { StatusChange } from "@/lib/documents/poll-schedule";
import { uploadFile, type UploadedDocument } from "@/lib/documents/upload-client";
import { DocumentRow } from "./document-row";
import { UploadZone } from "./upload-zone";
import { useDocumentPolling, type ListFetcher } from "./use-document-polling";
import { useUploader } from "./use-uploader";
import { meFromBody } from "@/lib/api/me-body";

export interface DocumentsViewProps {
  initial: ApiDocument[];
  hasLlmKey: boolean;
  chunks: { used: number; limit: number };
  /** Test seam. */
  fetchList?: ListFetcher;
}

function newDocument(u: UploadedDocument): ApiDocument {
  return {
    id: u.id,
    fileName: u.fileName,
    contentType: "",
    sizeBytes: 0,
    pageCount: null,
    chunkCount: 0,
    status: "Pending",
    failureReason: null,
    extractionNotice: null,
    createdAt: new Date().toISOString(),
  };
}

export function DocumentsView({
  initial,
  hasLlmKey,
  chunks: initialChunks,
  fetchList,
}: DocumentsViewProps) {
  const t = useTranslations("Documents");
  const tk = dynamicT(t);
  const locale = useLocale();
  const limits = limitsFor(hasLlmKey);
  const [docs, setDocs] = useState(initial);
  const [chunks, setChunks] = useState(initialChunks);
  const [refreshFailed, setRefreshFailed] = useState(false);
  const [announcement, setAnnouncement] = useState("");
  const [toDelete, setToDelete] = useState<ApiDocument | null>(null);
  const [deleting, setDeleting] = useState(false);
  const [deleteProblem, setDeleteProblem] = useState<ApiProblem | null>(null);
  const removed = useRef(new Set<string>());
  const heading = useRef<HTMLHeadingElement>(null);

  const refreshUsage = useCallback(async () => {
    const res = await bffFetch("/api/proxy/me", {}, meFromBody);
    if (res.ok) setChunks(res.data.chunks);
  }, []);

  const announce = useCallback(
    (changes: StatusChange[]) => {
      if (changes.length === 0) return;
      setAnnouncement(
        changes
          .map((c) => t("status.announce", { name: c.fileName, status: tk(`status.${c.to}`) }))
          .join(". "),
      );
    },
    [t, tk],
  );

  useDocumentPolling({
    documents: docs,
    removed,
    ...(fetchList ? { fetchList } : {}),
    onError: setRefreshFailed,
    onList: (list, changes) => {
      setDocs(list);
      announce(changes);
      if (changes.some((c) => c.to === "Ready" || c.to === "Failed")) void refreshUsage();
    },
  });

  const uploader = useUploader({
    limits,
    documentCount: docs.length,
    upload: uploadFile,
    onUploaded: (u) => {
      setDocs((all) => (all.some((d) => d.id === u.id) ? all : [newDocument(u), ...all]));
      setAnnouncement(t("upload.announceDone", { name: u.fileName }));
    },
  });

  const confirmDelete = async () => {
    if (!toDelete) return;
    setDeleting(true);
    setDeleteProblem(null);
    const res = await bffFetch(`/api/proxy/documents/${toDelete.id}`, { method: "DELETE" });
    setDeleting(false);
    if (!res.ok && res.problem.status !== 404) {
      setDeleteProblem(res.problem);
      return;
    }
    removed.current.add(toDelete.id);
    setDocs((all) => all.filter((d) => d.id !== toDelete.id));
    setAnnouncement(t("delete.announce", { name: toDelete.fileName }));
    setToDelete(null);
    void refreshUsage();
    heading.current?.focus(); // the opener is gone; keep focus in the list area
  };

  useEffect(() => {
    // A document that was deleted elsewhere (or by a failed retry) must not linger in `removed` forever.
    const ids = new Set(docs.map((d) => d.id));
    for (const id of removed.current) if (!ids.has(id)) removed.current.delete(id);
  }, [docs]);

  const atLimit = docs.length >= limits.maxDocuments;
  const chunkPct = chunks.limit > 0 ? chunks.used / chunks.limit : 0;

  return (
    <div className="space-y-8">
      <section
        aria-label={t("usage.documents", { count: docs.length, limit: limits.maxDocuments })}
        className="grid gap-4 sm:grid-cols-2"
      >
        <UsageMeter
          testId="meter-documents"
          label={t("usage.documents", { count: docs.length, limit: limits.maxDocuments })}
          value={docs.length}
          max={limits.maxDocuments}
        />
        {chunks.limit > 0 ? (
          <div className="space-y-1.5">
            <UsageMeter
              testId="meter-chunks"
              label={t("usage.chunks", { used: chunks.used, limit: chunks.limit })}
              value={chunks.used}
              max={chunks.limit}
            />
            {chunkPct > 0.8 ? (
              <p className="text-sm" data-testid="chunks-high">
                {t("usage.high")}
              </p>
            ) : null}
          </div>
        ) : null}
        <p className="text-sm text-muted-foreground sm:col-span-2">{t("usage.help")}</p>
      </section>

      <UploadZone
        items={uploader.items}
        limits={limits}
        disabledReason={atLimit ? "limit" : null}
        hasKey={hasLlmKey}
        onFiles={uploader.addFiles}
        onDismiss={uploader.dismiss}
        onClear={uploader.clearFinished}
      />

      <section aria-labelledby="list-title" className="space-y-3">
        <h2
          id="list-title"
          ref={heading}
          tabIndex={-1}
          className="text-lg font-semibold outline-none"
        >
          {t("list.title")}
        </h2>
        {refreshFailed ? (
          <p role="status" className="text-sm text-muted-foreground" data-testid="refresh-failed">
            {t("list.refreshFailed")}
          </p>
        ) : null}
        {docs.length === 0 ? (
          <div
            className="rounded-lg border border-dashed border-border p-6 text-center"
            data-testid="empty"
          >
            <p className="font-medium">{t("list.empty")}</p>
            <p className="text-sm text-muted-foreground">
              {t("list.emptyHelp", { size: formatBytes(limits.maxFileBytes, locale) })}
            </p>
          </div>
        ) : (
          <ul className="space-y-3">
            {docs.map((doc) => (
              <DocumentRow key={doc.id} doc={doc} onDelete={setToDelete} />
            ))}
          </ul>
        )}
      </section>

      <p role="status" aria-live="polite" className="sr-only" data-testid="live-region">
        {announcement}
      </p>

      <ConfirmDialog
        open={toDelete !== null}
        title={t("delete.title")}
        confirmLabel={deleting ? t("delete.deleting") : t("delete.confirm")}
        cancelLabel={t("delete.cancel")}
        pending={deleting}
        onConfirm={() => void confirmDelete()}
        onCancel={() => {
          setToDelete(null);
          setDeleteProblem(null);
        }}
        error={
          deleteProblem ? <ProblemAlert problem={deleteProblem} ctx={{ area: "delete" }} /> : null
        }
      >
        <p>
          {t.rich("delete.body", {
            name: toDelete?.fileName ?? "",
            file: (chunks) => (
              <bdi dir="auto" className="font-medium text-foreground">
                {chunks}
              </bdi>
            ),
          })}
        </p>
      </ConfirmDialog>
    </div>
  );
}
