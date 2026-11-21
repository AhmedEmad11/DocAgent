"use client";

import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/button";

// No stack traces or internals shown to users (UI-GEN-6). Message text is added to the catalogs when error states land.
export default function ErrorPage({ reset }: { error: Error; reset: () => void }) {
  const t = useTranslations("Error");
  return (
    <div className="max-w-xl space-y-4">
      <h1 className="text-3xl font-semibold tracking-tight">{t("title")}</h1>
      <p className="text-muted-foreground">{t("description")}</p>
      <Button onClick={reset}>{t("retry")}</Button>
    </div>
  );
}
