import { getLocale, getTranslations } from "next-intl/server";
import { signOutAction } from "@/lib/auth/actions";

/** A POST form (server action), so it works without JavaScript and cannot be triggered by a link (UI-SEC-3). */
export async function SignOutButton() {
  const t = await getTranslations("Auth");
  const locale = await getLocale();
  return (
    <form action={signOutAction}>
      <input type="hidden" name="locale" value={locale} />
      <button
        type="submit"
        className="inline-flex min-h-9 min-w-9 items-center justify-center rounded-md px-3 text-sm font-medium hover:bg-accent hover:text-accent-foreground focus-visible:ring-[3px] focus-visible:ring-ring/50 focus-visible:outline-none"
      >
        {t("signOut")}
      </button>
    </form>
  );
}
