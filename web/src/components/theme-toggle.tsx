"use client";

import { useEffect, useState } from "react";
import { useTranslations } from "next-intl";
import { Monitor, Moon, Sun } from "lucide-react";
import { Button } from "@/components/ui/button";
import {
  THEME_STORAGE_KEY,
  applyTheme,
  isThemeMode,
  nextThemeMode,
  type ThemeMode,
} from "@/lib/theme";

const icons = { system: Monitor, light: Sun, dark: Moon } as const;

export function ThemeToggle() {
  const t = useTranslations("Theme");
  const [mode, setMode] = useState<ThemeMode>("system");

  useEffect(() => {
    try {
      const stored = localStorage.getItem(THEME_STORAGE_KEY);
      if (isThemeMode(stored)) setMode(stored); // eslint-disable-line react-hooks/set-state-in-effect -- sync from browser storage after hydration
    } catch {
      /* storage unavailable: stay on system */
    }
  }, []);

  // Follow the OS while in "system" mode.
  useEffect(() => {
    if (mode !== "system") return;
    const mq = window.matchMedia("(prefers-color-scheme: dark)");
    const onChange = () => applyTheme("system");
    mq.addEventListener("change", onChange);
    return () => mq.removeEventListener("change", onChange);
  }, [mode]);

  function cycle() {
    const next = nextThemeMode(mode);
    setMode(next);
    applyTheme(next);
    try {
      localStorage.setItem(THEME_STORAGE_KEY, next);
    } catch {
      /* ignore */
    }
  }

  const Icon = icons[mode];
  return (
    <Button
      type="button"
      variant="ghost"
      size="icon"
      onClick={cycle}
      data-theme-mode={mode}
      aria-label={t("current", { mode: t(mode) })}
      title={t(mode)}
    >
      <Icon aria-hidden="true" />
    </Button>
  );
}
