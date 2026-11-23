"use client";

import { useRef, useState } from "react";
import { Upload, X } from "lucide-react";
import { useLocale, useTranslations } from "next-intl";
import { Button } from "@/components/ui/button";
import { useProblemText } from "@/components/ui/problem-alert";
import { Link } from "@/i18n/navigation";
import { formatBytes } from "@/lib/documents/format";
import { ACCEPT_ATTRIBUTE, type TierLimits } from "@/lib/documents/limits";
import type { UploadItem } from "./use-uploader";

function ItemMessage({ item, limits }: { item: UploadItem; limits: TierLimits }) {
  const t = useTranslations("Documents.upload");
  const te = useTranslations("Errors");
  const locale = useLocale();
  const describe = useProblemText();
  if (item.localReason === "unsupported") return <>{te("unsupported")}</>;
  if (item.localReason === "tooLarge")
    return <>{te("tooLarge", { size: formatBytes(limits.maxFileBytes, locale) })}</>;
  if (item.localReason === "empty") return <>{t("empty")}</>;
  if (item.localReason === "documentLimit")
    return <>{te("documentLimit", { limit: limits.maxDocuments })}</>;
  if (item.problem)
    return (
      <>
        {
          describe(item.problem, {
            area: "upload",
            documentLimit: limits.maxDocuments,
            maxBytes: limits.maxFileBytes,
          }).text
        }
      </>
    );
  return null;
}

export function UploadZone({
  items,
  limits,
  disabledReason,
  hasKey,
  onFiles,
  onDismiss,
  onClear,
}: {
  items: UploadItem[];
  limits: TierLimits;
  /** When set, upload is off and this explains why. */
  disabledReason: "limit" | "unconfirmed" | null;
  hasKey: boolean;
  onFiles: (files: File[]) => void;
  onDismiss: (id: number) => void;
  onClear: () => void;
}) {
  const t = useTranslations("Documents.upload");
  const locale = useLocale();
  const input = useRef<HTMLInputElement>(null);
  const [dragging, setDragging] = useState(false);
  const disabled = disabledReason !== null;
  const finished = items.some((i) => i.state === "done" || i.state === "failed");

  return (
    <section aria-labelledby="upload-title" className="space-y-3">
      <h2 id="upload-title" className="text-lg font-semibold">
        {t("title")}
      </h2>
      <div
        data-testid="drop-zone"
        data-dragging={dragging || undefined}
        onDragOver={(e) => {
          if (disabled) return;
          e.preventDefault();
          setDragging(true);
        }}
        onDragLeave={() => setDragging(false)}
        onDrop={(e) => {
          e.preventDefault();
          setDragging(false);
          if (!disabled) onFiles(Array.from(e.dataTransfer.files));
        }}
        className={`flex flex-col items-center gap-3 rounded-lg border-2 border-dashed p-6 text-center ${dragging ? "border-primary bg-primary/10" : "border-border"}`}
      >
        <Upload aria-hidden="true" className="size-6 text-muted-foreground" />
        <p>
          {dragging ? t("dragging") : t("drop")}{" "}
          <Button
            type="button"
            variant="link"
            className="h-auto min-h-6 p-0"
            disabled={disabled}
            onClick={() => input.current?.click()}
          >
            {t("choose")}
          </Button>
        </p>
        <input
          ref={input}
          type="file"
          multiple
          accept={ACCEPT_ATTRIBUTE}
          data-testid="file-input"
          tabIndex={-1}
          aria-hidden="true"
          className="sr-only"
          disabled={disabled}
          onChange={(e) => {
            onFiles(Array.from(e.target.files ?? []));
            e.target.value = "";
          }}
        />
        <p className="text-sm text-muted-foreground">
          {t("rules", { size: formatBytes(limits.maxFileBytes, locale) })}
        </p>
      </div>

      {disabledReason === "limit" ? (
        <p
          role="status"
          className="rounded-md border border-border bg-muted p-3 text-sm"
          data-testid="at-limit"
        >
          {t("atLimit", { limit: limits.maxDocuments })}{" "}
          {hasKey ? null : (
            <Link href="/settings" className="text-primary underline underline-offset-4">
              {t("atLimitDemo")}
            </Link>
          )}
        </p>
      ) : null}
      {disabledReason === "unconfirmed" ? (
        <p role="status" className="rounded-md border border-border bg-muted p-3 text-sm">
          {t("unconfirmed")}
        </p>
      ) : null}

      {items.length > 0 ? (
        <div className="space-y-2" data-testid="upload-queue">
          <div className="flex items-center justify-between gap-2">
            <h3 className="text-sm font-medium">{t("queueLabel")}</h3>
            {finished ? (
              <Button type="button" variant="ghost" size="sm" onClick={onClear}>
                {t("clear")}
              </Button>
            ) : null}
          </div>
          <ul className="space-y-2">
            {items.map((item) => (
              <li
                key={item.id}
                data-testid="upload-item"
                data-state={item.state}
                className="space-y-1.5 rounded-md border border-border p-3 text-sm"
              >
                <div className="flex items-center justify-between gap-2">
                  <span
                    dir="auto"
                    title={item.name}
                    className="min-w-0 flex-1 truncate font-medium"
                  >
                    {item.name}
                  </span>
                  <span className="shrink-0 text-muted-foreground">{t(item.state)}</span>
                  {item.state === "done" || item.state === "failed" ? (
                    <Button
                      type="button"
                      variant="ghost"
                      size="icon"
                      className="size-6 min-h-6 min-w-6"
                      onClick={() => onDismiss(item.id)}
                      aria-label={t("dismiss", { name: item.name })}
                    >
                      <X aria-hidden="true" />
                    </Button>
                  ) : null}
                </div>
                {item.state === "uploading" ? (
                  <progress
                    aria-label={t("progress", { name: item.name })}
                    className="meter"
                    value={Math.round(item.progress * 100)}
                    max={100}
                  />
                ) : null}
                {item.state === "failed" ? (
                  <p className="text-destructive" data-testid="upload-error">
                    <ItemMessage item={item} limits={limits} />
                  </p>
                ) : null}
              </li>
            ))}
          </ul>
        </div>
      ) : null}
    </section>
  );
}
