"use client";

import { Check, Circle } from "lucide-react";
import { useTranslations } from "next-intl";
import { passwordRules } from "@/lib/auth/schemas";

/** The password rules (D25), shown before submit with a text-and-icon state, not colour alone (UI-AUTH-1). */
export function PasswordRules({ value, id }: { value: string; id: string }) {
  const t = useTranslations("Auth.rules");
  return (
    <div id={id} className="space-y-1 text-sm">
      <p className="text-muted-foreground">{t("title")}</p>
      <ul className="space-y-0.5">
        {passwordRules.map((rule) => {
          const met = rule.test(value);
          return (
            <li key={rule.key} className="flex items-center gap-2" data-met={met}>
              {met ? (
                <Check aria-hidden className="size-4 text-primary" />
              ) : (
                <Circle aria-hidden className="size-4 text-muted-foreground" />
              )}
              <span>{t(rule.key)}</span>
              <span className="sr-only">{met ? t("met") : t("unmet")}</span>
            </li>
          );
        })}
      </ul>
    </div>
  );
}
