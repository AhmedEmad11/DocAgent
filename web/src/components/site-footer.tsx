import { useTranslations } from "next-intl";
import { Link } from "@/i18n/navigation";

export function SiteFooter() {
  const t = useTranslations("Footer");
  return (
    <footer className="border-t border-border">
      <div className="mx-auto max-w-5xl px-4 py-4 text-sm">
        <Link href="/privacy" className="text-muted-foreground underline-offset-4 hover:underline">
          {t("privacy")}
        </Link>
      </div>
    </footer>
  );
}
