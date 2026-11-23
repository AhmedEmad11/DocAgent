import AxeBuilder from "@axe-core/playwright";
import { expect, test } from "@playwright/test";
import {
  mock,
  mockState,
  newUser,
  seedDocuments,
  setDemo,
  signInAndLand,
  signIn,
  uniqueEmail,
} from "./helpers";

test.describe("account (UI-ACC-1 to 4)", () => {
  test("shows email, confirmation state, sign-in methods and mode", async ({ page }) => {
    const email = await newUser("acc");
    await setDemo(email, { used: 1, limit: 10 });
    await signInAndLand(page, email);
    await page.goto("/en/account");
    await expect(page.getByRole("heading", { level: 1 })).toHaveText("Account");
    await expect(page.getByTestId("account-email")).toHaveText(email);
    await expect(page.getByTestId("account-confirmed")).toHaveText("Confirmed");
    await expect(page.getByTestId("account-methods")).toHaveText("None (email and password)");
    await expect(page.getByTestId("account-mode")).toContainText("Demo mode");

    await page.context().clearCookies();
    const linked = uniqueEmail("linked");
    await mock("/__test/user", { email: linked, externalLogins: ["Google", "GitHub"] });
    await signInAndLand(page, linked);
    await page.goto("/en/account");
    await expect(page.getByTestId("account-methods")).toHaveText("Google, GitHub");
  });

  test("an own key shows as the mode with provider and model; an unconfirmed account can ask for the link again", async ({
    page,
  }) => {
    const email = await newUser("accmode");
    await signInAndLand(page, email);
    await page.goto("/en/settings");
    await page.getByTestId("key-input").fill("sk-example-only-not-a-real-key-123456");
    await page.getByLabel("Model", { exact: true }).fill("gpt-4.1");
    await page.getByRole("button", { name: "Check and save key" }).click();
    await expect(page.getByTestId("saved-last4")).toBeVisible();
    await page.goto("/en/account");
    await expect(page.getByTestId("account-mode")).toContainText("Your own API key");
    await expect(page.getByTestId("account-mode")).toContainText("openrouter / gpt-4.1");

    await page.context().clearCookies();
    await signInAndLand(page, "unconfirmed@example.com");
    await page.goto("/en/account");
    await expect(page.getByTestId("account-confirmed")).toHaveText("Not confirmed yet");
    await expect(page.getByTestId("unconfirmed")).toBeVisible();
  });

  test("storage usage as a meter with text, a warning above 80% and the way to free space", async ({
    page,
  }) => {
    const email = await newUser("accmeter");
    await seedDocuments(email, [{ fileName: "a.txt", status: "Ready" }], { chunksUsed: 2700 });
    await signInAndLand(page, email);
    await page.goto("/en/account");
    await expect(page.getByTestId("meter-chunks")).toContainText(
      "Storage: 2,700 of 3,000 text chunks",
    );
    await expect(page.getByTestId("meter-chunks").getByRole("progressbar")).toHaveAttribute(
      "value",
      "2700",
    );
    await expect(page.getByTestId("chunks-high")).toContainText("more than 80%");
    await expect(page.getByText("Deleting a document frees its chunks.")).toBeVisible();
    await seedDocuments(email, [{ fileName: "a.txt", status: "Ready" }], { chunksUsed: 100 });
    await page.reload();
    await expect(page.getByTestId("chunks-high")).toHaveCount(0);
  });

  test("delete account: the dialog lists what goes, needs the typed email, then clears the session and says goodbye; signing in again fails and a new account starts empty", async ({
    page,
    context,
  }) => {
    const email = await newUser("accdel");
    await seedDocuments(email, [{ fileName: "mine.pdf", status: "Ready" }]);
    await signInAndLand(page, email);
    await page.goto("/en/account");

    const opener = page.getByTestId("delete-account");
    await opener.click();
    const dialog = page.getByRole("dialog");
    for (const text of [
      "all your documents",
      "their text chunks and embeddings",
      "your saved API key",
      "your demo usage",
      "This cannot be undone.",
    ])
      await expect(dialog).toContainText(text);
    const confirm = dialog.getByRole("button", { name: "Delete my account" });
    await expect(confirm).toBeDisabled();
    await dialog.getByTestId("delete-email").fill("someone-else@example.com");
    await expect(confirm).toBeDisabled();

    // Cancel first: nothing is deleted.
    await page.keyboard.press("Escape");
    await expect(dialog).toBeHidden();
    await expect(opener).toBeFocused();
    expect(
      (await mockState()).keyCalls.filter((c) => c.op === "delete-account" && c.email === email),
    ).toHaveLength(0);

    await opener.click();
    await dialog.getByTestId("delete-email").fill(email.toUpperCase());
    await expect(confirm).toBeEnabled();
    await confirm.click();
    await expect(page).toHaveURL(/\/en\/goodbye$/);
    await expect(page.getByRole("heading", { level: 1 })).toHaveText("Your account was deleted");
    expect(
      (await mockState()).keyCalls.filter((c) => c.op === "delete-account" && c.email === email),
    ).toHaveLength(1);

    // The session is gone: protected pages send to sign-in; the old credentials no longer work.
    const cookies = await context.cookies();
    expect(cookies.find((c) => c.name.includes("docagent_session"))).toBeUndefined();
    await page.goto("/en/documents");
    await expect(page).toHaveURL(/\/en\/sign-in/);
    await signIn(page, email);
    await expect(
      page.getByRole("alert").filter({ hasText: "Email or password is incorrect" }),
    ).toBeVisible();
  });

  test("Arabic account page and goodbye page", async ({ page }) => {
    const email = await newUser("accar");
    await signInAndLand(page, email);
    await page.goto("/ar/account");
    await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
    await expect(page.getByRole("heading", { level: 1 })).toHaveText("الحساب");
    await page.getByTestId("delete-account").click();
    await expect(page.getByRole("dialog")).toContainText("لا يمكن التراجع عن ذلك.");
    await page.getByTestId("delete-email").fill(email);
    await page.getByRole("dialog").getByRole("button", { name: "حذف حسابي" }).click();
    await expect(page).toHaveURL(/\/ar\/goodbye$/);
    await expect(page.getByRole("heading", { level: 1 })).toHaveText("تم حذف حسابك");
  });

  for (const locale of ["en", "ar"]) {
    for (const scheme of ["light", "dark"] as const) {
      test(`axe: account and goodbye (${locale}, ${scheme}), with the dialog open`, async ({
        page,
      }) => {
        await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });
        const email = await newUser(`axe${locale}${scheme}`);
        await seedDocuments(email, [{ fileName: "a.txt", status: "Ready" }], { chunksUsed: 2700 });
        await signInAndLand(page, email);
        await page.goto(`/${locale}/account`);
        const serious = async () =>
          (await new AxeBuilder({ page }).analyze()).violations.filter(
            (v) => v.impact === "serious" || v.impact === "critical",
          );
        expect(await serious()).toEqual([]);
        await page.getByTestId("delete-account").click();
        await expect(page.getByRole("dialog")).toBeVisible();
        expect(await serious()).toEqual([]);
        await page.keyboard.press("Escape");
        await page.goto(`/${locale}/goodbye`);
        expect(await serious()).toEqual([]);
      });
    }
  }

  test("reflows at 320 px", async ({ page }) => {
    await page.setViewportSize({ width: 320, height: 700 });
    const email = await newUser("narrow");
    await signInAndLand(page, email);
    await page.goto("/en/account");
    await expect(page.getByTestId("account-details")).toBeVisible();
    expect(
      await page.evaluate(
        () => document.documentElement.scrollWidth - document.documentElement.clientWidth,
      ),
    ).toBeLessThanOrEqual(0);
  });
});
