import { getTranslations } from "next-intl/server";
import { Button } from "@/components/ui/button";
import { Link } from "@/i18n/navigation";

export default async function LandingPage() {
  const t = await getTranslations("Landing");

  return (
    <div className="max-w-2xl space-y-6">
      <h1 className="text-3xl font-semibold tracking-tight text-balance sm:text-4xl">
        {t("title")}
      </h1>
      <p className="text-lg text-muted-foreground">{t("description")}</p>
      <p>{t("signInRequired")}</p>
      <div className="flex flex-wrap gap-3">
        <Button asChild>
          <Link href="/sign-in">{t("signIn")}</Link>
        </Button>
        <Button variant="outline" asChild>
          <Link href="/register">{t("register")}</Link>
        </Button>
      </div>
      <p>
        <Link href="/privacy" className="text-primary underline underline-offset-4">
          {t("privacyLink")}
        </Link>
      </p>
    </div>
  );
}
