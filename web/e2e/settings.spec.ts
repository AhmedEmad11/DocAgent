import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page, type Response } from "@playwright/test";
import { mockState, newUser, setDemo, signInAndLand } from "./helpers";

const KEY = "sk-example-only-not-a-real-key-123456";
/** The mock answers this one slowly, so the "checking" state can be seen. */
const SLOW_KEY = "sk-SLOWKEY-example-only-not-a-real-key-3456";

async function demoUser(prefix: string) {
  const email = await newUser(prefix);
  await setDemo(email, { used: 2, limit: 10 });
  return email;
}

async function openSettings(page: Page, email: string, locale = "en") {
  await signInAndLand(page, email);
  await page.goto(`/${locale}/settings`);
  await expect(page.getByTestId("key-form")).toBeVisible();
}

async function fill(page: Page, key: string, model = "gpt-4.1", provider = "openai") {
  await page.getByLabel("Provider").selectOption(provider);
  await page.getByTestId("key-input").fill(key);
  await page.getByLabel("Model", { exact: true }).fill(model);
}

const save = (page: Page, name = "Check and save key") =>
  page.getByRole("button", { name }).click();
const alert = (page: Page) => page.getByTestId("problem-alert");

test.describe("key settings (UI-KEY-1 to 6)", () => {
  test("a demo user adds a key: models listed with the key in a header, saved, the field emptied, the banner goes, the key is nowhere in the browser", async ({
    page,
  }) => {
    const email = await demoUser("keysave");
    const urls: string[] = [];
    const bodies: string[] = [];
    const consoleText: string[] = [];
    page.on("request", (r) => urls.push(r.url()));
    page.on("console", (m) => consoleText.push(m.text()));
    page.on("response", async (r: Response) => {
      try {
        bodies.push(JSON.stringify(await r.allHeaders()), await r.text());
      } catch {
        /* no body */
      }
    });

    await openSettings(page, email);
    await expect(page.getByTestId("demo-banner")).toBeVisible();
    await expect(page.getByTestId("no-key")).toContainText("free demo model with daily limits");
    const keyInput = page.getByTestId("key-input");
    await expect(keyInput).toHaveAttribute("type", "password");
    await expect(keyInput).toHaveAttribute("autocomplete", "off");
    await expect(keyInput).toHaveValue("");

    await page.getByLabel("Provider").selectOption("openrouter");
    await keyInput.fill(SLOW_KEY);
    await page.getByRole("button", { name: "Load models" }).click();
    await expect(page.getByText(/Choose from 3 models/)).toBeVisible();
    const optionValues = await page
      .locator("datalist option")
      .evaluateAll((os) => os.map((o) => o.getAttribute("value")));
    expect(optionValues).toEqual([
      "openai/gpt-4.1-mini",
      "anthropic/claude-sonnet-4",
      "meta-llama/llama-3.3-70b",
    ]);
    await page.getByLabel("Model", { exact: true }).fill("openai/gpt-4.1-mini");
    await save(page);

    await expect(page.getByTestId("saving")).toBeVisible();
    await expect(keyInput).toHaveValue(""); // emptied as soon as it is sent
    await expect(page.getByTestId("notice")).toContainText("Your key was checked and saved.");
    await expect(page.getByTestId("saved-last4")).toHaveText("••••3456");
    await expect(page.getByTestId("saved-model")).toHaveText("openai/gpt-4.1-mini");
    await expect(page.getByTestId("saved-provider")).toHaveText("openrouter");
    await expect(page.getByTestId("demo-banner")).toHaveCount(0); // demo -> own key (UI-DEMO-4)
    await expect(page.getByRole("button", { name: "Check and replace key" })).toBeVisible();

    // The key reached the BFF in a header (models) and the body (save), never a URL; nothing came back with it.
    const state = await mockState();
    const mine = state.keyCalls.filter((c) => c.provider === "openrouter" || c.op === "save");
    expect(mine.find((c) => c.op === "models")).toMatchObject({
      keyInHeader: true,
      urlHadKey: false,
      auth: "bearer",
    });
    expect(mine.find((c) => c.op === "save")).toMatchObject({
      keyInBody: true,
      urlHadKey: false,
      auth: "bearer",
    });
    expect(urls.join("\n")).not.toContain("SLOWKEY");
    expect(bodies.join("\n")).not.toContain("SLOWKEY");
    expect(consoleText.join("\n")).not.toContain("SLOWKEY");
    const inBrowser = await page.evaluate(() =>
      JSON.stringify({
        html: document.documentElement.outerHTML,
        cookie: document.cookie,
        local: { ...localStorage },
        session: { ...sessionStorage },
      }),
    );
    expect(inBrowser).not.toContain("SLOWKEY");
    expect(page.url()).not.toContain("SLOWKEY");

    // The new mode applies: documents show the 100-document limit.
    await page.goto("/en/documents");
    await expect(page.getByText("Documents: 0 of 100")).toBeVisible();
  });

  test("provider refusals: each code has its message; the key field is emptied; focus goes to the field it is about", async ({
    page,
  }) => {
    await openSettings(page, await demoUser("keyerr"));
    await fill(page, "BADKEY-0123456789");
    await save(page);
    await expect(alert(page)).toContainText(
      "The provider did not accept this key. Check that you copied it fully and that it is for openai.",
    );
    await expect(page.getByTestId("key-input")).toHaveValue("");
    await expect(page.getByTestId("key-input")).toBeFocused();

    await fill(page, "UNKNOWNMODEL-0123456", "bad-model");
    await save(page);
    await expect(alert(page)).toContainText(
      "That model is not available for this key. Choose another model.",
    );
    await expect(page.getByLabel("Model", { exact: true })).toBeFocused();

    for (const [key, text] of [
      ["NOCREDIT-0123456789", "Your openai account has no credit left."],
      ["RATELIMITED-0123456", "Too many requests. Try again in 12 seconds."],
      ["DOWN-0123456789", "The model provider is not responding. Try again in a minute."],
      ["REJECTED-0123456789", "The provider could not process this request."],
    ] as const) {
      await fill(page, key);
      await save(page);
      await expect(alert(page)).toContainText(text);
      await expect(page.getByTestId("key-input")).toHaveValue("");
    }
    await expect(page.getByTestId("notice")).toHaveCount(0); // nothing was saved
    await expect(page.getByTestId("saved-last4")).toHaveCount(0);
  });

  test("field validation comes before any call; the model list falls back to typing when it cannot be loaded", async ({
    page,
  }) => {
    await openSettings(page, await demoUser("keyval"));
    await save(page);
    await expect(page.getByText("Enter your API key.")).toBeVisible();
    await expect(page.getByTestId("key-input")).toBeFocused();
    await page.getByTestId("key-input").fill("short");
    await save(page);
    await expect(page.getByText("The key is too short.")).toBeVisible();
    expect(
      (await mockState()).keyCalls.filter((c) => c.op === "save" && c.model === ""),
    ).toHaveLength(0); // no call was made

    await expect(page.getByRole("button", { name: "Load models" })).toBeDisabled();
    await page.getByTestId("key-input").fill("LISTERR-0123456789");
    await page.getByRole("button", { name: "Load models" }).click();
    await expect(page.getByTestId("models-failed")).toContainText(
      "The provider could not process this request.",
    );
    await expect(page.getByTestId("models-failed")).toContainText(
      "You can still type the model name yourself.",
    );
    await page.getByLabel("Model", { exact: true }).fill("my-own-model");
    await expect(page.getByLabel("Model", { exact: true })).toHaveValue("my-own-model");

    await page.getByTestId("key-input").fill("BADKEY-0123456789");
    await page.getByRole("button", { name: "Load models" }).click();
    await expect(page.getByTestId("models-failed")).toContainText(
      "The provider did not accept this key",
    );
  });

  test("replace a saved key; then delete it with the consequences stated, and the demo banner returns", async ({
    page,
  }) => {
    const email = await demoUser("keyflow");
    await openSettings(page, email);
    await fill(page, KEY, "gpt-4.1", "openai");
    await save(page);
    await expect(page.getByTestId("saved-last4")).toHaveText("••••3456");

    await page.getByLabel("Provider").selectOption("anthropic");
    await page.getByTestId("key-input").fill("sk-example-only-replacement-9999");
    await page.getByLabel("Model", { exact: true }).fill("claude-sonnet-4");
    await save(page, "Check and replace key");
    await expect(page.getByTestId("saved-last4")).toHaveText("••••9999");
    await expect(page.getByTestId("saved-provider")).toHaveText("anthropic");
    await expect(page.getByTestId("saved-model")).toHaveText("claude-sonnet-4");
    await expect(page.getByTestId("demo-banner")).toHaveCount(0);

    const opener = page.getByRole("button", { name: "Delete my key" });
    await opener.click();
    const dialog = page.getByRole("dialog");
    await expect(dialog).toContainText("Questions use the demo model and its daily limits again.");
    await expect(dialog).toContainText(
      "Documents above the demo limits stay, but you cannot upload new ones until you are under the limit.",
    );
    await page.keyboard.press("Escape");
    await expect(opener).toBeFocused();
    await expect(page.getByTestId("saved-last4")).toBeVisible(); // cancelled: still saved

    await opener.click();
    await dialog.getByRole("button", { name: "Delete key" }).click();
    await expect(page.getByTestId("notice")).toContainText("Your key was deleted.");
    await expect(page.getByTestId("no-key")).toBeVisible();
    await expect(page.getByTestId("demo-banner")).toBeVisible(); // demo again
    await page.reload();
    await expect(page.getByTestId("no-key")).toBeVisible();
  });

  test("an account without a key while the demo is off sees a hint on every page", async ({
    page,
  }) => {
    const email = await newUser("nodemo");
    await signInAndLand(page, email);
    await expect(page.getByTestId("no-key-banner")).toContainText(
      "no API key saved and the demo is not available",
    );
    await page.goto("/en/settings");
    await expect(page.getByTestId("no-key")).toContainText("the demo is not available");
  });

  test("an unconfirmed account is told to confirm first", async ({ page }) => {
    await signInAndLand(page, "unconfirmed@example.com");
    await page.goto("/en/settings");
    await expect(page.getByTestId("unconfirmed")).toBeVisible();
    await expect(page.getByTestId("key-form")).toHaveCount(0);
  });
});

test.describe("key settings: Arabic and accessibility", () => {
  test("Arabic: labels, errors and the saved key stay readable; key and model fields are left to right", async ({
    page,
  }) => {
    await openSettings(page, await demoUser("keyar"), "ar");
    await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
    await expect(page.getByRole("heading", { level: 1 })).toHaveText("الإعدادات");
    await expect(page.getByTestId("key-input")).toHaveAttribute("dir", "ltr");
    await page.getByTestId("key-input").fill("BADKEY-0123456789");
    await page.getByLabel("النموذج", { exact: true }).fill("gpt-4.1");
    await page.getByRole("button", { name: "تحقق واحفظ المفتاح" }).click();
    await expect(page.getByTestId("problem-alert")).toContainText("لم يقبل المزوّد هذا المفتاح");
    await page.getByTestId("key-input").fill(KEY);
    await page.getByRole("button", { name: "تحقق واحفظ المفتاح" }).click();
    await expect(page.getByTestId("saved-last4")).toHaveText("••••3456");
    await expect(page.getByTestId("saved-last4")).toHaveAttribute("dir", "ltr");
  });

  for (const locale of ["en", "ar"]) {
    for (const scheme of ["light", "dark"] as const) {
      test(`axe: settings (${locale}, ${scheme}) empty, saved, with an error and with the dialog open`, async ({
        page,
      }) => {
        await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });
        await openSettings(page, await demoUser(`axe${locale}${scheme}`), locale);
        const serious = async () =>
          (await new AxeBuilder({ page }).analyze()).violations.filter(
            (v) => v.impact === "serious" || v.impact === "critical",
          );
        expect(await serious()).toEqual([]);

        await page.getByTestId("key-input").fill("BADKEY-0123456789");
        await page.getByLabel(/^(Model|النموذج)$/).fill("gpt-4.1");
        await page.getByRole("button", { name: /Check and save key|تحقق واحفظ المفتاح/ }).click();
        await expect(page.getByTestId("problem-alert")).toBeVisible();
        expect(await serious()).toEqual([]);

        await page.getByTestId("key-input").fill(KEY);
        await page.getByRole("button", { name: /Check and save key|تحقق واحفظ المفتاح/ }).click();
        await expect(page.getByTestId("saved-last4")).toBeVisible();
        expect(await serious()).toEqual([]);
        await page.getByRole("button", { name: /Delete my key|حذف مفتاحي/ }).click();
        await expect(page.getByRole("dialog")).toBeVisible();
        expect(await serious()).toEqual([]);
      });
    }
  }

  test("reflows at 320 px", async ({ page }) => {
    await page.setViewportSize({ width: 320, height: 700 });
    await openSettings(page, await demoUser("narrow"));
    const overflow = await page.evaluate(
      () => document.documentElement.scrollWidth - document.documentElement.clientWidth,
    );
    expect(overflow).toBeLessThanOrEqual(0);
  });
});
