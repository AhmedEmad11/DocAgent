"use client";

import { useEffect, useRef, type ReactNode } from "react";
import { Button } from "@/components/ui/button";

interface ConfirmDialogProps {
  open: boolean;
  title: string;
  children: ReactNode;
  confirmLabel: string;
  cancelLabel: string;
  pending?: boolean;
  /** Shown inside the dialog (an alert) when the action failed. */
  error?: ReactNode;
  destructive?: boolean;
  onConfirm: () => void;
  onCancel: () => void;
}

/**
 * Modal confirmation on the native `<dialog>`: the browser traps focus, closes on Escape, marks the rest inert and
 * returns focus to the opener (UI-A11Y-1). No dependency; logical properties only, so it mirrors in Arabic.
 * Cancel has the initial focus, so a stray Enter never confirms a destructive action.
 */
export function ConfirmDialog({
  open,
  title,
  children,
  confirmLabel,
  cancelLabel,
  pending = false,
  error,
  destructive = true,
  onConfirm,
  onCancel,
}: ConfirmDialogProps) {
  const ref = useRef<HTMLDialogElement>(null);

  useEffect(() => {
    const dialog = ref.current;
    if (!dialog) return;
    if (open && !dialog.open) dialog.showModal();
    if (!open && dialog.open) dialog.close();
  }, [open]);

  return (
    <dialog
      ref={ref}
      aria-labelledby="confirm-title"
      onCancel={(e) => {
        e.preventDefault(); // we close it ourselves through `open`
        if (!pending) onCancel();
      }}
      className="m-auto w-[calc(100%-2rem)] max-w-md rounded-lg border border-border bg-background p-6 text-foreground shadow-lg backdrop:bg-black/50"
    >
      <div className="space-y-4">
        <h2 id="confirm-title" className="text-lg font-semibold">
          {title}
        </h2>
        <div className="space-y-2 text-sm text-muted-foreground">{children}</div>
        {error}
        <div className="flex flex-wrap justify-end gap-2">
          <Button type="button" variant="outline" onClick={onCancel} disabled={pending} autoFocus>
            {cancelLabel}
          </Button>
          <Button
            type="button"
            onClick={onConfirm}
            disabled={pending}
            aria-busy={pending}
            className={
              destructive
                ? "bg-destructive text-destructive-foreground hover:bg-destructive/90"
                : ""
            }
          >
            {confirmLabel}
          </Button>
        </div>
      </div>
    </dialog>
  );
}
