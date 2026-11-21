export const THEME_STORAGE_KEY = "theme";
export type ThemeMode = "system" | "light" | "dark";
export const themeModes: readonly ThemeMode[] = ["system", "light", "dark"];

export function nextThemeMode(mode: ThemeMode): ThemeMode {
  return themeModes[(themeModes.indexOf(mode) + 1) % themeModes.length]!;
}

export function isThemeMode(value: unknown): value is ThemeMode {
  return value === "system" || value === "light" || value === "dark";
}

export function applyTheme(mode: ThemeMode, root: HTMLElement = document.documentElement) {
  const dark =
    mode === "dark" ||
    (mode === "system" && window.matchMedia("(prefers-color-scheme: dark)").matches);
  root.classList.toggle("dark", dark);
}

// Runs before first paint (inline, nonce'd) so there is no flash of the wrong theme.
export const themeInitScript = `(function(){try{var m=localStorage.getItem("${THEME_STORAGE_KEY}");var d=m==="dark"||((m!=="light")&&window.matchMedia("(prefers-color-scheme: dark)").matches);document.documentElement.classList.toggle("dark",d)}catch(e){}})();`;
