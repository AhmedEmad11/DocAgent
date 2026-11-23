import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { newUser, seedDocuments, setDemo, signInAndLand } from "./helpers";

// UI-A11Y-1 to 8 across the whole UI: every page in both languages and both themes (axe), titles, landmarks, keyboard use, focus
// visibility, target sizes, reduced motion, reflow at 320 px and 200% zoom, and text spacing overrides.

const PUBLIC = [
  "",
  "/privacy",
  "/sign-in",
  "/register",
  "/forgot-password",
  "/reset-password",
  "/check-email",
  "/confirm?userId=u&code=c",
  "/goodbye",
  "/no-such-page",
];
const SIGNED_IN = ["/documents", "/ask", "/summary", "/compare", "/settings", "/account"];

async function serious(page: Page) {
  const results = await new AxeBuilder({ page }).analyze();
  return results.violations
    .filter((v) => v.impact === "serious" || v.impact === "critical")
    .map((v) => ({
      id: v.id,
      impact: v.impact,
      nodes: v.nodes.slice(0, 3).map((n) => n.target.join(" ")),
    }));
}

async function richUser(prefix: string) {
  const email = await newUser(prefix);
  await seedDocuments(email, [
    { fileName: "lease.pdf", status: "Ready", extractionNotice: "A note." },
    { fileName: "عقد.docx", status: "Ready" },
    {
      fileName: "scan.pdf",
      status: "Failed",
      failureReason: "No extractable text: scanned PDFs are not supported.",
    },
    { fileName: "later.md", status: "Pending" },
  ]);
  await setDemo(email, { used: 4, limit: 10 });
  return email;
}

for (const locale of ["en", "ar"]) {
  for (const scheme of ["light", "dark"] as const) {
    test.describe(`axe on every page (${locale}, ${scheme})`, () => {
      test.beforeEach(async ({ page }) => {
        await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });
      });

      test("public pages", async ({ page }) => {
        for (const path of PUBLIC) {
          await page.goto(`/${locale}${path}`);
          await expect(page.getByRole("main")).toBeVisible();
          expect(await serious(page), path || "/").toEqual([]);
        }
      });

      test("public form pages with their error summaries open", async ({ page }) => {
        for (const path of ["/sign-in", "/register", "/forgot-password", "/reset-password"]) {
          await page.goto(`/${locale}${path}`);
          await page.locator("main form button[type=submit]").click();
          await expect(page.getByTestId("form-error")).toBeVisible();
          expect(await serious(page), path).toEqual([]);
        }
      });

      test("signed-in pages", async ({ page }) => {
        const email = await richUser(`axe${locale}${scheme}`);
        await signInAndLand(page, email);
        for (const path of SIGNED_IN) {
          await page.goto(`/${locale}${path}`);
          await expect(page.getByRole("main")).toBeVisible();
          expect(await serious(page), path).toEqual([]);
        }
      });
    });
  }
}

test.describe("page structure (UI-A11Y-7)", () => {
  test("every page has a unique title, one h1, landmarks, and the right lang and dir", async ({
    page,
  }) => {
    const email = await richUser("structure");
    for (const locale of ["en", "ar"] as const) {
      const titles = new Map<string, string>();
      const check = async (path: string) => {
        await page.goto(`/${locale}${path}`);
        const title = await page.title();
        expect(title, path).not.toBe("");
        expect(
          titles.get(title),
          `${path} shares its title with ${titles.get(title)}`,
        ).toBeUndefined();
        titles.set(title, path || "/");
        await expect(page.locator("h1"), path).toHaveCount(1);
        await expect(page.getByRole("banner"), path).toHaveCount(1);
        await expect(page.getByRole("navigation"), path).toHaveCount(1);
        await expect(page.locator("main"), path).toHaveCount(1);
        await expect(page.locator("html")).toHaveAttribute("lang", locale);
        await expect(page.locator("html")).toHaveAttribute("dir", locale === "ar" ? "rtl" : "ltr");
      };
      for (const path of PUBLIC.filter((p) => p !== "/no-such-page")) await check(path);
      await signInAndLand(page, email);
      for (const path of SIGNED_IN) await check(path);
      await page.context().clearCookies();
    }
  });
});

test.describe("keyboard (UI-A11Y-1)", () => {
  test("the skip link is the first stop and moves focus to the main content", async ({ page }) => {
    await page.goto("/en/privacy");
    await page.keyboard.press("Tab");
    const skip = page.getByRole("link", { name: "Skip to content" });
    await expect(skip).toBeFocused();
    await expect(skip).toBeVisible();
    await page.keyboard.press("Enter");
    await expect(page.locator("main")).toBeFocused();
  });

  test("sign in with the keyboard alone", async ({ page }) => {
    await page.goto("/en/sign-in");
    await page.getByLabel("Email").focus();
    await page.keyboard.type("confirmed@example.com");
    await page.keyboard.press("Tab");
    await expect(page.getByLabel("Password")).toBeFocused();
    await page.keyboard.type("Example-Only-1");
    await page.keyboard.press("Enter");
    await expect(page).toHaveURL(/\/en\/documents$/);
  });

  test("a failed form puts focus on the error summary; its links lead to the fields", async ({
    page,
  }) => {
    await page.goto("/en/register");
    await page.getByRole("button", { name: "Create account" }).click();
    const summary = page.getByTestId("form-error");
    await expect(summary).toBeFocused();
    await expect(summary).toContainText("There is a problem with the form:");
    await expect(summary.getByRole("link")).toHaveCount(2);
    await expect(page.getByLabel("Email")).toHaveAttribute("aria-invalid", "true");
    await expect(page.getByLabel("Email")).toHaveAccessibleDescription(
      /Enter your email address\./,
    );
    await summary.getByRole("link", { name: /Password/ }).click();
    await expect(page.getByLabel("Password")).toBeFocused();
  });

  test("a failed sign-in moves focus to the alert with the message; Arabic too", async ({
    page,
  }) => {
    for (const [locale, button, text] of [
      ["en", "Sign in", "Email or password is incorrect"],
      ["ar", "تسجيل الدخول", "غير صحيحة"],
    ] as const) {
      await page.goto(`/${locale}/sign-in`);
      await page.locator("#field-email").fill("nobody@example.com");
      await page.locator("#field-password").fill("Example-Wrong-3");
      await page.getByRole("button", { name: button, exact: true }).click();
      const alert = page.getByTestId("form-error");
      await expect(alert).toBeFocused();
      await expect(alert).toContainText(text);
    }
  });

  test("a dialog traps focus, starts on the safe button, and returns focus to its opener", async ({
    page,
  }) => {
    const email = await richUser("dialog");
    await signInAndLand(page, email);
    const opener = page.getByRole("button", { name: "Delete lease.pdf" });
    await opener.focus();
    await page.keyboard.press("Enter");
    const dialog = page.getByRole("dialog");
    await expect(dialog.getByRole("button", { name: "Cancel" })).toBeFocused();
    // Focus may leave the page for the browser's own controls (activeElement is then the body) but never reaches the page behind the dialog.
    const inside = async () =>
      dialog.evaluate(
        (d) => document.activeElement === document.body || d.contains(document.activeElement),
      );
    for (let i = 0; i < 6; i++) {
      await page.keyboard.press("Tab");
      expect(await inside(), `Tab ${i + 1} stays inside the dialog`).toBe(true);
    }
    for (let i = 0; i < 3; i++) {
      await page.keyboard.press("Shift+Tab");
      expect(await inside()).toBe(true);
    }
    await page.keyboard.press("Escape");
    await expect(dialog).toBeHidden();
    await expect(opener).toBeFocused();
  });

  test("no keyboard trap: Tab moves through every control on the documents page and every focus stop shows a focus indicator", async ({
    page,
  }) => {
    const email = await richUser("tabs");
    await signInAndLand(page, email);
    const seen = new Set<string>();
    for (let i = 0; i < 80; i++) {
      await page.keyboard.press("Tab");
      const info = await page.evaluate(() => {
        const el = document.activeElement as HTMLElement | null;
        if (!el || el === document.body) return null;
        const s = getComputedStyle(el);
        const visible =
          s.boxShadow !== "none" || (s.outlineStyle !== "none" && parseFloat(s.outlineWidth) > 0);
        const r = el.getBoundingClientRect();
        return {
          key: `${el.tagName}:${el.textContent?.trim().slice(0, 30)}:${el.getAttribute("href") ?? el.id}`,
          visible,
          inViewport: r.width > 0 && r.height > 0,
        };
      });
      if (info === null) break; // focus left the page: the cycle is complete
      expect(info.visible, `focus indicator on ${info.key}`).toBe(true);
      expect(info.inViewport, info.key).toBe(true);
      seen.add(info.key);
    }
    expect(seen.size).toBeGreaterThan(15);
  });

  test("ask with the keyboard: type, submit with Enter on the button, read the answer", async ({
    page,
  }) => {
    const email = await richUser("askkb");
    await signInAndLand(page, email);
    await page.goto("/en/ask");
    await page.getByLabel("Your question").focus();
    await page.keyboard.type("when is rent due?");
    await page.getByRole("button", { name: "Ask", exact: true }).focus();
    await page.keyboard.press("Enter");
    await expect(page.getByTestId("answer")).toBeVisible();
  });
});

test.describe("sizes, motion and reflow (UI-A11Y-5, UI-A11Y-6)", () => {
  test("buttons, text fields and selects are at least 24 by 24 CSS pixels; checkboxes and radios at least 20 with spacing", async ({
    page,
  }) => {
    const email = await richUser("targets");
    await signInAndLand(page, email);
    for (const path of ["/documents", "/ask", "/settings", "/account", "/compare"]) {
      await page.goto(`/en${path}`);
      if (path === "/ask")
        await page.getByRole("radio", { name: "Only the documents I choose" }).check();
      const small = await page.evaluate(() =>
        [
          ...document.querySelectorAll<HTMLElement>(
            "button, summary, input:not([type=checkbox]):not([type=radio]):not([type=file]), select, textarea",
          ),
        ]
          .filter(
            (el) =>
              el.offsetParent !== null &&
              getComputedStyle(el).visibility !== "hidden" &&
              !el.classList.contains("sr-only"),
          )
          .map((el) => ({
            el: `${el.tagName} ${el.textContent?.trim().slice(0, 20) ?? ""}`,
            r: el.getBoundingClientRect(),
          }))
          .filter(({ r }) => r.width < 24 || r.height < 24)
          .map(({ el, r }) => `${el} ${Math.round(r.width)}x${Math.round(r.height)}`),
      );
      expect(small, path).toEqual([]);
      const boxes = await page.evaluate(() =>
        [...document.querySelectorAll<HTMLElement>("input[type=checkbox], input[type=radio]")].map(
          (el) => {
            const r = el.getBoundingClientRect();
            return Math.min(r.width, r.height);
          },
        ),
      );
      for (const size of boxes) expect(size, `${path} checkbox/radio`).toBeGreaterThanOrEqual(20);
    }
  });

  test("with reduced motion no animation or transition runs longer than a blink", async ({
    page,
  }) => {
    await page.emulateMedia({ reducedMotion: "reduce" });
    const email = await richUser("motion");
    await seedDocuments(email, [{ fileName: "work.pdf", status: "Processing" }]);
    await signInAndLand(page, email);
    const durations = await page.evaluate(() => {
      const out: number[] = [];
      for (const el of document.querySelectorAll<HTMLElement>("*")) {
        const s = getComputedStyle(el);
        out.push(
          parseFloat(s.animationDuration) * (s.animationDuration.endsWith("ms") ? 1 : 1000),
          parseFloat(s.transitionDuration) * (s.transitionDuration.endsWith("ms") ? 1 : 1000),
        );
      }
      return out.filter((n) => Number.isFinite(n));
    });
    expect(Math.max(...durations)).toBeLessThanOrEqual(1);
  });

  test("every page reflows at 320 px and at 200% zoom (640 px) without horizontal scrolling, also with text spacing overrides", async ({
    page,
  }) => {
    const email = await richUser("reflow");
    await signInAndLand(page, email);
    // WCAG 1.4.12 text spacing: line height 1.5, paragraph spacing 2x, letter spacing .12em, word spacing .16em.
    const spacing =
      "*{line-height:1.5 !important;letter-spacing:.12em !important;word-spacing:.16em !important} p{margin-block-end:2em !important}";
    for (const width of [320, 640]) {
      await page.setViewportSize({ width, height: 800 });
      for (const locale of ["en", "ar"]) {
        for (const path of [...SIGNED_IN, "/privacy", "/sign-in", "/register"]) {
          await page.goto(`/${locale}${path}`);
          await page.evaluate((css) => {
            // CSSOM, not a <style> element: the CSP forbids inline style elements without the nonce.
            const sheet = new CSSStyleSheet();
            sheet.replaceSync(css);
            document.adoptedStyleSheets = [...document.adoptedStyleSheets, sheet];
          }, spacing);
          const overflow = await page.evaluate(
            () => document.documentElement.scrollWidth - document.documentElement.clientWidth,
          );
          expect(overflow, `${locale}${path} at ${width}px`).toBeLessThanOrEqual(0);
        }
      }
    }
  });
});
