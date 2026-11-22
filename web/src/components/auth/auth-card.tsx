import type { ReactNode } from "react";

/** Shared frame of the auth pages: one h1, optional description, content. */
export function AuthCard({
  title,
  description,
  children,
}: {
  title: string;
  description?: string | undefined;
  children: ReactNode;
}) {
  return (
    <div className="mx-auto w-full max-w-md space-y-6">
      <header className="space-y-2">
        <h1 className="text-3xl font-semibold tracking-tight">{title}</h1>
        {description ? <p className="text-muted-foreground">{description}</p> : null}
      </header>
      {children}
    </div>
  );
}

export function Notice({
  children,
  tone = "info",
}: {
  children: ReactNode;
  tone?: "info" | "error";
}) {
  return (
    <div
      role={tone === "error" ? "alert" : "status"}
      className={`rounded-md border p-3 text-sm ${tone === "error" ? "border-destructive/50 bg-destructive/10" : "border-border bg-muted"}`}
    >
      {children}
    </div>
  );
}
