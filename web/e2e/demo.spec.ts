import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { KNOWN_PRIVACY, newUser, seedDocuments, setDemo, signInAndLand } from "./helpers";

async function demoUser(
  prefix: string,
  demo: Parameters<typeof setDemo>[1] = { used: 3, limit: 10 },
) {
  const email = await newUser(prefix);
  await seedDocuments(email, [
    { fileName: "lease.pdf", status: "Ready" },
    { fileName: "addendum.docx", status: "Ready" },
  ]);
  await setDemo(email, demo);
  return email;
}

const ask = async (page: Page, question: string) => {
  await page.getByLabel("Your question").fill(question);
  await page.getByRole("button", { name: "Ask", exact: true }).click();
};
const allowance = (page: Page) => page.getByTestId("demo-allowance");

test.describe("demo banner (UI-DEMO-1 to 4)", () => {
  test("shows the live allowance from the API, the reset time and the link to the key settings on every signed-in page; absent when signed out", async ({
    page,
  }) => {
    const email = await demoUser("banner", {
      used: 3,
      limit: 10,
      resetsAt: "2026-11-24T18:05:00Z",
    });
    await page.goto("/en/sign-in");
    await expect(page.getByTestId("demo-banner")).toHaveCount(0);
    await signInAndLand(page, email);
    await expect(allowance(page)).toContainText(
      "Demo mode: 7 of 10 questions left today. Resets at",
    );
    await expect(
      page.getByTestId("demo-banner").getByRole("link", { name: "Add your own API key" }),
    ).toHaveAttribute("href", "/en/settings");
    for (const path of ["/en/ask", "/en/settings", "/en/account", "/en/summary"]) {
      await page.goto(path);
      await expect(allowance(page)).toContainText("7 of 10 questions left");
    }
  });

  test("the numbers are the API's, not constants", async ({ page }) => {
    const email = await demoUser("numbers", { used: 1, limit: 25 });
    await signInAndLand(page, email);
    await expect(allowance(page)).toContainText("24 of 25 questions left");
  });

  test.describe("time zone", () => {
    test.use({ timezoneId: "Asia/Tokyo", locale: "en-US" });
    test("the reset time is shown in the visitor's local time, not the server's", async ({
      page,
    }) => {
      const email = await demoUser("tz", { used: 3, limit: 10, resetsAt: "2026-11-24T18:05:00Z" });
      await signInAndLand(page, email);
      await expect(allowance(page)).toContainText(/Resets at 3:05\s?AM/); // 18:05 UTC is 03:05 the next day in Tokyo
    });
  });

  test("the privacy notice opens the first time in a browser session, then stays closed but one click away; in Arabic it is translated", async ({
    page,
  }) => {
    const email = await demoUser("privacy");
    await signInAndLand(page, email);
    const details = page.getByTestId("demo-privacy");
    await expect(details).toHaveJSProperty("open", true);
    await expect(page.getByTestId("demo-privacy-text")).toHaveText(KNOWN_PRIVACY);
    await page.waitForTimeout(3200); // it counts as seen once it stayed on screen for a few seconds
    await page.goto("/en/ask");
    await expect(details).toHaveJSProperty("open", false);
    await details.locator("summary").click();
    await expect(page.getByTestId("demo-privacy-text")).toBeVisible();

    await page.goto("/ar/ask");
    await details.locator("summary").click();
    await expect(page.getByTestId("demo-privacy-text")).toContainText("وضع التجربة");
    await expect(page.getByTestId("demo-privacy-text")).not.toContainText(
      "third-party language model",
    ); // the English sentence is not shown
  });

  test("an unknown notice text from the API is shown as given", async ({ page }) => {
    const email = await demoUser("othernotice", {
      used: 0,
      limit: 10,
      privacyNotice: "A new notice from the API.",
    });
    await signInAndLand(page, email);
    await expect(page.getByTestId("demo-privacy-text")).toHaveText("A new notice from the API.");
  });

  test("the banner follows every question, summary and comparison; every demo answer carries the notice", async ({
    page,
  }) => {
    const email = await demoUser("follow");
    await signInAndLand(page, email);
    await page.goto("/en/ask");
    await expect(allowance(page)).toContainText("7 of 10");
    await ask(page, "when is rent due?");
    await expect(page.getByTestId("privacy-notice").first()).toHaveText(KNOWN_PRIVACY);
    await expect(page.getByTestId("answer-meta")).toContainText("Answered by the free demo model");
    await expect(allowance(page)).toContainText("6 of 10"); // re-read from /me after the answer

    await page.goto("/en/summary");
    await page.getByLabel("Document").selectOption({ label: "lease.pdf" });
    await page.getByRole("button", { name: "Summarize" }).click();
    await expect(page.getByTestId("result")).toBeVisible();
    await expect(allowance(page)).toContainText("5 of 10");

    await page.goto("/en/compare");
    await page.getByLabel("First document").selectOption({ label: "lease.pdf" });
    await page.getByLabel("Second document").selectOption({ label: "addendum.docx" });
    await page.getByRole("button", { name: "Compare", exact: true }).click();
    await expect(page.getByTestId("result")).toBeVisible();
    await expect(allowance(page)).toContainText("4 of 10");
  });

  test("at zero the controls are off with the reset time and a link to add a key; the API limit message also works", async ({
    page,
  }) => {
    const email = await demoUser("quota", { used: 9, limit: 10, resetsAt: "2026-11-24T18:05:00Z" });
    await signInAndLand(page, email);
    await page.goto("/en/ask");
    await ask(page, "last one");
    await expect(page.getByTestId("answer")).toBeVisible();
    await expect(allowance(page)).toContainText("you have used all 10 questions for today");
    await expect(allowance(page)).toContainText("They reset at");

    // Ask: submit off, explanation with the reset time and the link.
    await expect(page.getByRole("button", { name: "Ask", exact: true })).toBeDisabled();
    await expect(
      page.getByRole("alert").filter({ hasText: "You have used today's demo questions." }),
    ).toContainText("They reset at");
    await expect(
      page.getByRole("alert").getByRole("link", { name: "Add an API key" }),
    ).toHaveAttribute("href", "/en/settings");
    for (const [path, button] of [
      ["/en/summary", "Summarize"],
      ["/en/compare", "Compare"],
    ] as const) {
      await page.goto(path);
      await expect(page.getByRole("button", { name: button, exact: true })).toBeDisabled();
      await expect(page.getByTestId("problem-alert")).toContainText(
        "You have used today's demo questions.",
      );
    }
  });

  test("a stale page still gets the API's own refusal when the limit was reached elsewhere", async ({
    page,
  }) => {
    const email = await demoUser("stale", { used: 5, limit: 10 });
    await signInAndLand(page, email);
    await page.goto("/en/ask");
    await setDemo(email, { used: 10, limit: 10 }); // another tab used the rest
    await ask(page, "one more");
    await expect(page.getByTestId("problem-alert").first()).toContainText(
      "You have used today's demo questions.",
    );
    await expect(page.getByTestId("problem-alert").first()).toContainText("They reset at");
  });

  test("demo to own key and back: the banner, the notice and the answer mode follow", async ({
    page,
  }) => {
    const email = await demoUser("transition");
    await signInAndLand(page, email);
    await page.goto("/en/settings");
    await page.getByTestId("key-input").fill("sk-example-only-not-a-real-key-123456");
    await page.getByLabel("Model", { exact: true }).fill("gpt-4.1");
    await page.getByRole("button", { name: "Check and save key" }).click();
    await expect(page.getByTestId("saved-last4")).toBeVisible();
    await expect(page.getByTestId("demo-banner")).toHaveCount(0);

    await page.goto("/en/ask");
    await ask(page, "with my key");
    await expect(page.getByTestId("answer-meta")).toContainText("Answered with your own key");
    await expect(page.getByTestId("privacy-notice")).toHaveCount(0);

    await page.goto("/en/settings");
    await page.getByRole("button", { name: "Delete my key" }).click();
    await page.getByRole("dialog").getByRole("button", { name: "Delete key" }).click();
    await expect(page.getByTestId("demo-banner")).toBeVisible();
    await expect(allowance(page)).toContainText("of 10 questions left");
  });

  test("Arabic banner: translated, mirrored, Western digits", async ({ page }) => {
    const email = await demoUser("ar");
    await signInAndLand(page, email);
    await page.goto("/ar/documents");
    await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
    await expect(allowance(page)).toContainText("وضع التجربة: تبقّى 7 من 10 أسئلة اليوم");
    expect(await allowance(page).innerText()).not.toMatch(/[٠-٩]/);
  });

  for (const locale of ["en", "ar"]) {
    for (const scheme of ["light", "dark"] as const) {
      test(`axe: pages with the banner (${locale}, ${scheme}), open and exhausted`, async ({
        page,
      }) => {
        await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });
        const email = await demoUser(`axe${locale}${scheme}`);
        await signInAndLand(page, email);
        const serious = async () =>
          (await new AxeBuilder({ page }).analyze()).violations.filter(
            (v) => v.impact === "serious" || v.impact === "critical",
          );
        for (const path of ["documents", "ask", "settings", "account"]) {
          await page.goto(`/${locale}/${path}`);
          await expect(page.getByTestId("demo-banner")).toBeVisible();
          expect(await serious(), path).toEqual([]);
        }
        await setDemo(email, { used: 10, limit: 10 });
        await page.goto(`/${locale}/ask`);
        await expect(page.getByTestId("demo-allowance")).toBeVisible();
        expect(await serious()).toEqual([]);
      });
    }
  }
});
