"use client";

import { Trash2 } from "lucide-react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { Button } from "@/components/ui/button";
import { dynamicT } from "@/i18n/dynamic";
import { Link } from "@/i18n/navigation";
import type { ApiDocument } from "@/lib/api/documents-schema";
import { formatBytes } from "@/lib/documents/format";
import { documentKind } from "@/lib/documents/limits";
import { StatusBadge } from "./status-badge";

/** Picks the hint for a failed document from the API's reason (UI-DOC-5); the reason itself is shown as given. */
export function failureHint(reason: string | null): "scanned" | "storage" | "other" {
  const r = (reason ?? "").toLowerCase();
  if (r.includes("scanned")) return "scanned";
  if (r.includes("storage limit")) return "storage";
  return "other";
}

export function DocumentRow({
  doc,
  onDelete,
  extraActions,
}: {
  doc: ApiDocument;
  onDelete: (doc: ApiDocument) => void;
  extraActions?: React.ReactNode;
}) {
  const t = useTranslations("Documents");
  const tk = dynamicT(t);
  const format = useFormatter();
  const locale = useLocale();
  const meta = [
    tk(`list.kind.${documentKind(doc.contentType)}`),
    formatBytes(doc.sizeBytes, locale),
    doc.pageCount !== null ? t("list.pages", { count: doc.pageCount }) : null,
    doc.status === "Ready" ? t("list.chunks", { count: doc.chunkCount }) : null,
  ].filter(Boolean);

  return (
    <li
      data-testid="doc-row"
      data-status={doc.status}
      className="space-y-2 rounded-lg border border-border p-4"
    >
      <div className="flex flex-wrap items-start justify-between gap-x-4 gap-y-2">
        <div className="min-w-0 flex-1 basis-48">
          {/* File names are text, any script or direction, and may be very long (UI-DOC-9). */}
          <p
            dir="auto"
            title={doc.fileName}
            className="truncate font-medium"
            data-testid="doc-name"
          >
            {doc.fileName}
          </p>
          <p className="text-sm text-muted-foreground">
            {meta.join(" · ")}
            {" · "}
            {t.rich("list.uploaded", {
              date: format.dateTime(new Date(doc.createdAt), "short"),
              time: (chunks) => (
                <time dateTime={doc.createdAt} suppressHydrationWarning>
                  {chunks}
                </time>
              ),
            })}
          </p>
        </div>
        <StatusBadge status={doc.status} />
      </div>

      {doc.status === "Failed" ? (
        <div className="space-y-1 text-sm" data-testid="doc-failed">
          {doc.failureReason ? (
            <p>
              <span className="font-medium">{t("failed.reason")}</span>{" "}
              <span dir="auto">{doc.failureReason}</span>
            </p>
          ) : null}
          <p className="text-muted-foreground">{tk(`failed.${failureHint(doc.failureReason)}`)}</p>
        </div>
      ) : null}

      {doc.extractionNotice ? (
        <p
          className="rounded-md border border-border bg-muted p-2 text-sm"
          data-testid="doc-notice"
        >
          <span className="font-medium">{t("notice.label")}</span>{" "}
          <span dir="auto">{doc.extractionNotice}</span>
        </p>
      ) : null}

      <div className="flex flex-wrap gap-2">
        {doc.status === "Ready" ? (
          <Button asChild variant="outline" size="sm">
            <Link href={{ pathname: "/ask", query: { doc: doc.id } }}>{t("actions.ask")}</Link>
          </Button>
        ) : null}
        {doc.status === "Ready" ? (
          <>
            <Button asChild variant="outline" size="sm">
              <Link href={{ pathname: "/summary", query: { doc: doc.id } }}>
                {t("actions.summarize")}
              </Link>
            </Button>
            <Button asChild variant="outline" size="sm">
              <Link href={{ pathname: "/compare", query: { a: doc.id } }}>
                {t("actions.compare")}
              </Link>
            </Button>
          </>
        ) : null}
        {extraActions}
        <Button
          type="button"
          variant="outline"
          size="sm"
          onClick={() => onDelete(doc)}
          aria-label={t("delete.actionFor", { name: doc.fileName })}
        >
          <Trash2 aria-hidden="true" />
          {t("delete.action")}
        </Button>
      </div>
    </li>
  );
}
