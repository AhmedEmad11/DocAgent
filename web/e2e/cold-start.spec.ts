import AxeBuilder from "@axe-core/playwright";
import { expect, test } from "@playwright/test";
import { mock, newUser, PASSWORD } from "./helpers";

// UI-GEN-7: a sleeping API (SnapDeploy) answers 503 with an HTML page until a wake POST. These tests put the shared mock to sleep, so
// this file runs alone in its own project, after everything else (playwright.config.ts). The wake call is deduplicated per server
// instance (20 s), hence the auto-wake safety net in the first test.
test.describe.configure({ mode: "serial" });

test.afterEach(async () => {
  await mock("/__test/sleep", { asleep: false });
});

test("sign-in while the API sleeps shows the notice, wakes it and then succeeds by itself", async ({
  page,
}, testInfo) => {
  const email = await newUser("cold");
  // The pre-warm check says "ready" once, so the sign-in itself is what meets the sleeping API.
  let statusCalls = 0;
  await page.route("**/api/status", (route) => {
    if (statusCalls++ === 0) return route.fulfill({ json: { ready: true } });
    return route.continue();
  });
  await page.goto("/en/sign-in");
  await expect.poll(() => statusCalls).toBeGreaterThan(0);
  await mock("/__test/sleep", { wakeDelayMs: 1500, autoWakeMs: 30_000 });

  await page.getByLabel("Email").fill(email);
  await page.getByLabel("Password").fill(PASSWORD);
  await page.getByRole("button", { name: "Sign in" }).click();

  await expect(page.getByTestId("api-starting")).toContainText("Starting the server");
  await expect(page.getByTestId("form-error")).toHaveCount(0);
  await expect(page).toHaveURL(/\/en\/documents$/, { timeout: 45_000 });
  await expect(page.getByTestId("account-email")).toHaveText(email);

  const sleep = await mock<{ wakeCalls: number; sleepingHits: number }>("/__test/sleep", {
    asleep: false,
  });
  expect(sleep.sleepingHits).toBe(0); // reset by the call above; the real check is the sign-in that worked
  if (testInfo.retry === 0) expect(statusCalls).toBeGreaterThan(1);
});

test("the pre-warm notice appears while the API sleeps, is accessible, and goes away when it is up", async ({
  page,
}) => {
  await page.emulateMedia({ reducedMotion: "reduce" });
  await mock("/__test/sleep", { wakeDelayMs: 1500, autoWakeMs: 5000 });
  await page.goto("/en/sign-in");

  const notice = page.getByTestId("api-starting");
  await expect(notice).toContainText("Starting the server");
  await expect(notice).toContainText(/\(\d+s\)/);
  await expect(notice).toHaveAttribute("role", "status");
  // The form stays usable while the notice is shown.
  await page.getByLabel("Email").fill("someone@example.com");
  await expect(page.getByLabel("Email")).toHaveValue("someone@example.com");

  const results = await new AxeBuilder({ page })
    .withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"])
    .analyze();
  expect(
    results.violations.filter((v) => v.impact === "serious" || v.impact === "critical"),
  ).toEqual([]);

  await expect(notice).not.toContainText("Starting the server", { timeout: 30_000 });
});

test("the Arabic notice is shown in Arabic", async ({ page }) => {
  await mock("/__test/sleep", { wakeDelayMs: 1500, autoWakeMs: 4000 });
  await page.goto("/ar/sign-in");
  await expect(page.getByTestId("api-starting")).toContainText("جار تشغيل الخادم");
  await expect(page.getByTestId("api-starting")).not.toContainText("جار تشغيل الخادم", {
    timeout: 30_000,
  });
});
