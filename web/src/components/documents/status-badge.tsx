import { CircleCheck, CircleX, Clock, LoaderCircle } from "lucide-react";
import { useTranslations } from "next-intl";
import type { DocumentStatus } from "@/lib/api/documents-schema";

const STYLE: Record<DocumentStatus, string> = {
  Pending: "border-border bg-muted text-foreground",
  Processing: "border-primary/40 bg-primary/10 text-foreground",
  Ready: "border-border bg-muted text-foreground",
  Failed: "border-destructive/60 bg-destructive/10 text-foreground",
};

const ICON = {
  Pending: Clock,
  Processing: LoaderCircle,
  Ready: CircleCheck,
  Failed: CircleX,
} as const;

/** Status as text plus an icon, never colour alone (UI-DOC-4, UI-A11Y-2). */
export function StatusBadge({ status }: { status: DocumentStatus }) {
  const t = useTranslations("Documents.status");
  const Icon = ICON[status];
  return (
    <span
      data-testid="doc-status"
      data-status={status}
      className={`inline-flex items-center gap-1.5 rounded-full border px-2.5 py-0.5 text-xs font-medium ${STYLE[status]}`}
    >
      <Icon
        aria-hidden="true"
        className={`size-3.5 ${status === "Processing" ? "animate-spin" : ""}`}
      />
      {t(status)}
    </span>
  );
}
