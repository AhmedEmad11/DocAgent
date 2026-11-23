import AxeBuilder from "@axe-core/playwright";
import { expect, test } from "@playwright/test";

const locales = [
  { locale: "en", dir: "ltr", h1: /Ask your documents/ },
  { locale: "ar", dir: "rtl", h1: /اسأل مستنداتك/ },
] as const;

for (const { locale, dir, h1 } of locales) {
  test.describe(`/${locale}`, () => {
    test(`renders with lang=${locale} dir=${dir}`, async ({ page }) => {
      const csp: string[] = [];
      page.on("console", (m) => {
        if (/content security policy/i.test(m.text())) csp.push(m.text());
      });
      await page.goto(`/${locale}`);
      const html = page.locator("html");
      await expect(html).toHaveAttribute("lang", locale);
      await expect(html).toHaveAttribute("dir", dir);
      await expect(page.getByRole("heading", { level: 1 })).toHaveText(h1);
      await expect(page.getByRole("banner").getByText("DocAgent")).toBeVisible();
      await expect(page.getByRole("navigation")).toBeVisible();
      expect(csp).toEqual([]);
    });

    test("has no serious or critical axe violations in light and dark", async ({ page }) => {
      for (const scheme of ["light", "dark"] as const) {
        await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });
        for (const path of [`/${locale}`, `/${locale}/privacy`]) {
          await page.goto(path);
          await expect(page.locator("html")).toHaveClass(
            scheme === "dark" ? /dark/ : /^((?!dark).)*$/,
          );
          const results = await new AxeBuilder({ page })
            .withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"])
            .analyze();
          const bad = results.violations.filter(
            (v) => v.impact === "serious" || v.impact === "critical",
          );
          expect(bad, JSON.stringify(bad.map((v) => v.id))).toEqual([]);
        }
      }
    });

    test("has no horizontal scroll at 320 px", async ({ page }) => {
      await page.setViewportSize({ width: 320, height: 700 });
      await page.goto(`/${locale}`);
      const overflow = await page.evaluate(
        () => document.documentElement.scrollWidth - document.documentElement.clientWidth,
      );
      expect(overflow).toBeLessThanOrEqual(0);
    });
  });
}

test("the language switcher swaps locale and keeps the page", async ({ page }) => {
  await page.goto("/en/privacy");
  await page.getByRole("link", { name: /Switch language to/ }).click();
  await expect(page).toHaveURL(/\/ar\/privacy$/);
  await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
  await page.getByRole("link", { name: /تغيير اللغة إلى English/ }).click();
  await expect(page).toHaveURL(/\/en\/privacy$/);
});

test("the root redirects by Accept-Language and unknown locale paths show the 404", async ({
  browser,
}) => {
  const ctx = await browser.newContext({ locale: "ar-EG" });
  const page = await ctx.newPage();
  await page.goto("/");
  await expect(page).toHaveURL(/\/ar$/);
  await ctx.close();

  const p2 = await browser.newPage();
  const res = await p2.goto("/en/does-not-exist");
  expect(res?.status()).toBe(404);
  await expect(p2.getByRole("heading", { level: 1 })).toHaveText("Page not found");
  await p2.close();
});

test("the theme toggle switches the theme class", async ({ page }) => {
  await page.emulateMedia({ colorScheme: "light" });
  await page.goto("/en");
  const toggle = page.locator("[data-theme-mode]");
  await toggle.click(); // light
  await toggle.click(); // dark
  await expect(page.locator("html")).toHaveClass(/dark/);
  await page.reload();
  await expect(page.locator("html")).toHaveClass(/dark/);
});

test("security headers are sent (UI-SEC-4)", async ({ request }) => {
  const res = await request.get("/en");
  const h = res.headers();
  expect(h["x-content-type-options"]).toBe("nosniff");
  expect(h["referrer-policy"]).toBe("no-referrer");
  expect(h["cross-origin-opener-policy"]).toBe("same-origin");
  expect(h["strict-transport-security"]).toContain("max-age=");
  expect(h["permissions-policy"]).toContain("camera=()");
  expect(h["x-powered-by"]).toBeUndefined();

  const csp = h["content-security-policy"] ?? "";
  expect(csp).toMatch(/script-src 'self' 'nonce-[^']+' 'strict-dynamic'/);
  expect(csp).not.toContain("unsafe-eval");
  expect(csp).toContain("frame-ancestors 'none'");
  expect(csp).toContain("connect-src 'self'");
  // The nonce is per request.
  const again = (await request.get("/en")).headers()["content-security-policy"];
  expect(again).not.toBe(csp);
});

test("the API URL and secrets do not appear in the HTML", async ({ request }) => {
  const body = await (await request.get("/en")).text();
  expect(body).not.toContain("4010");
  expect(body).not.toMatch(/SESSION_SECRET|BFF_SHARED_SECRET/);
});
