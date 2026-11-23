import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page, type Response } from "@playwright/test";
import { mockState, newUser, seedDocuments, signInAndLand } from "./helpers";

async function readyUser(prefix: string, extra: { hasKey?: boolean } = {}) {
  const email = await newUser(prefix);
  await seedDocuments(
    email,
    [
      { fileName: "lease.pdf", status: "Ready" },
      { fileName: "notes.md", status: "Ready" },
      { fileName: "pending.txt", status: "Pending" },
    ],
    extra,
  );
  return email;
}

async function openAsk(page: Page, email: string, locale = "en") {
  await signInAndLand(page, email);
  await page.goto(`/${locale}/ask`);
  await expect(page.getByTestId("ask-form")).toBeVisible();
}

const ask = async (page: Page, question: string, button = "Ask") => {
  await page.getByLabel(/Your question|سؤالك/).fill(question);
  await page.getByRole("button", { name: button, exact: true }).click();
};

const alertText = (page: Page) => page.getByTestId("problem-alert");

test.describe("ask with streaming (UI-QA-1 to 12)", () => {
  test("streams progress, then a cited answer; markers jump to the citation; no token reaches the browser", async ({
    page,
  }) => {
    const email = await readyUser("stream");
    const seen: string[] = [];
    const pending: Promise<void>[] = [];
    page.on("response", (r: Response) =>
      pending.push(
        (async () => {
          seen.push(r.url(), JSON.stringify(await r.allHeaders()));
          try {
            seen.push(await r.text());
          } catch {
            /* no body */
          }
        })(),
      ),
    );
    let askHeaders: Record<string, string> = {};
    page.on("request", async (r) => {
      if (r.url().endsWith("/api/ask/stream")) askHeaders = await r.allHeaders();
    });
    await openAsk(page, email);

    await ask(page, "slow: when is rent due?");
    // Progress arrives while the stream is still open (UI-PERF-3): the first stage shows long before the answer.
    await expect(page.getByTestId("stage")).toHaveText("Searching your documents");
    await expect(page.getByTestId("answer")).toHaveCount(0);
    await expect(page.getByTestId("stage")).toHaveText("Checking sources");
    await expect(page.getByTestId("live-region")).toContainText("Checking sources");
    await expect(page.getByTestId("stage")).toHaveText("Verifying the answer");
    await expect(page.getByRole("button", { name: "Ask", exact: true })).toBeDisabled();

    const view = page.getByTestId("answer");
    await expect(view).toBeVisible();
    await expect(view).toContainText("Rent is due on the first day of each month");
    await expect(page.getByTestId("citation")).toHaveCount(2);
    await expect(page.getByTestId("citation").first()).toContainText("lease.pdf");
    await expect(page.getByTestId("citation").first()).toContainText("Page 3");
    await expect(page.getByTestId("citation").first()).toContainText(
      "Rent is payable on the first day of each month.",
    );
    await expect(page.getByTestId("answer-meta")).toContainText("Answered with your own key");
    await expect(page.getByTestId("answer-meta")).toContainText("openrouter / example/test-model");
    await expect(page.getByLabel("Your question")).toHaveValue("");
    await expect(page.getByTestId("live-region")).toContainText("The answer is ready.");

    // Inline [2] links to citation 2 and moves focus there.
    await view.getByTestId("citation-marker").nth(1).click();
    await expect(page.locator("#a1-cite-2")).toBeFocused();

    await Promise.all(pending);
    expect(seen.join("\n")).not.toMatch(/MOCKAT|MOCKRT/);
    expect(askHeaders.authorization).toBeUndefined(); // the browser never sends a token; the BFF adds it
    expect(askHeaders.cookie).toContain("docagent_session");
    const body = (await mockState()).ask.bodies.find((b) => b.question.startsWith("slow: when"));
    expect(body).toMatchObject({ documentIds: null, auth: "bearer" });
  });

  test("not found, incomplete, notices and the demo privacy notice are shown distinctly", async ({
    page,
  }) => {
    await openAsk(page, await readyUser("states"));
    await ask(page, "notfound please");
    await expect(page.getByTestId("answer-not-found")).toContainText(
      "This is not in your documents.",
    );
    await ask(page, "incomplete please");
    await expect(page.getByTestId("answer-incomplete").first()).toContainText("may be incomplete");
    await ask(page, "notice please");
    await expect(page.getByTestId("answer-notices").first()).toContainText(
      "Arabic text was repaired after extraction",
    );
    await ask(page, "demomode please");
    await expect(page.getByTestId("answer-meta").first()).toContainText(
      "Answered by the free demo model",
    );
    await expect(
      page.getByTestId("answer-notices").first().getByRole("listitem").first(),
    ).toContainText("Demo mode: your questions");
    // Newest first, all four kept in this tab (OI-12).
    await expect(page.getByTestId("qa-item")).toHaveCount(4);
    await expect(page.getByTestId("qa-question").first()).toHaveText("demomode please");
  });

  test("Arabic and mixed-direction questions and answers keep their own direction; citation markers still link", async ({
    page,
  }) => {
    await openAsk(page, await readyUser("arabic"));
    await ask(page, "متى يستحق الإيجار؟");
    await expect(page.getByTestId("qa-question")).toHaveAttribute("dir", "auto");
    await expect(page.getByTestId("answer")).toContainText("يستحق الإيجار في اليوم الأول");
    const dirs = await page
      .getByTestId("answer")
      .locator("p")
      .evaluateAll((ps) => ps.map((p) => getComputedStyle(p).direction));
    expect(dirs).toContain("rtl");
    expect(dirs).toContain("ltr"); // the English paragraph inside the same Arabic answer
    await expect(page.getByTestId("citation").first()).toContainText("البند 4");
    await expect(page.getByTestId("answer").getByTestId("citation-marker")).toHaveCount(2);
  });

  test("prompt-injected HTML, scripts, images and javascript: links in an answer or quote are inert (UI-SEC-8)", async ({
    page,
  }) => {
    await openAsk(page, await readyUser("inject"));
    await ask(page, "inject please");
    const view = page.getByTestId("answer");
    await expect(view).toContainText("Safe text");
    expect(
      await page.evaluate(() => (window as unknown as { __pwned?: number }).__pwned),
    ).toBeUndefined();
    await expect(view.locator("script, img, iframe, [onerror], [onclick]")).toHaveCount(0);
    const hrefs = await view
      .locator("a")
      .evaluateAll((as) => as.map((a) => a.getAttribute("href")));
    expect(hrefs.filter((h) => !h?.startsWith("#"))).toEqual(["https://example.com/page"]);
    const external = view.locator('a[href="https://example.com/page"]');
    await expect(external).toHaveAttribute("rel", "noopener noreferrer");
    // A quote is plain text, markup and all.
    await expect(page.getByTestId("citation").first()).toContainText("<b>markup</b>");
    await expect(page.getByTestId("citation").first().locator("b, a")).toHaveCount(0);
  });

  test("a marker without a matching citation stays plain text", async ({ page }) => {
    await openAsk(page, await readyUser("badcite"));
    await ask(page, "badcitation please");
    await expect(page.getByTestId("answer")).toContainText("a marker without a source [9]");
    await expect(page.getByTestId("answer").getByTestId("citation-marker")).toHaveCount(1);
  });

  test("errors before the stream: each code shows its message, the question stays, and nothing is lost", async ({
    page,
  }) => {
    await openAsk(page, await readyUser("errors"));
    const cases: [string, string][] = [
      ["quota", "You have used today's demo questions."],
      ["nokey", "Add an API key in Settings to ask questions."],
      ["badkey", "Your saved key was rejected by the provider. Replace it in Settings."],
      ["nocredit", "account has no credit left"],
      ["unreadable", "We could not read your saved key. Please save it again."],
      ["rejected", "The provider could not process this request."],
      ["unknownmodel", "That model is not available for this key."],
      ["demooff", "The demo is unavailable right now."],
      ["busy429", "You already have the maximum number of questions running."],
    ];
    for (const [word, text] of cases) {
      await ask(page, `${word} case`);
      await expect(alertText(page).first()).toContainText(text);
      await expect(page.getByLabel("Your question")).toHaveValue(`${word} case`); // kept for the retry
    }
    await expect(page.getByRole("link", { name: "Add an API key" }).first()).toHaveAttribute(
      "href",
      "/en/settings",
    );
  });

  test("errors during the stream: rate limit with a countdown, timeout, and a dropped connection; retry works", async ({
    page,
  }) => {
    await openAsk(page, await readyUser("mid"));
    await ask(page, "ratelimit case");
    await expect(alertText(page)).toContainText("The free demo models are busy. Try again in");
    await expect(page.getByRole("button", { name: /Try again in \d seconds?/ })).toBeDisabled();
    await expect(page.getByRole("button", { name: "Try again", exact: true })).toBeEnabled({
      timeout: 6000,
    });

    await ask(page, "timeout case");
    await expect(alertText(page).first()).toContainText(
      "The question took too long. Try a narrower question or fewer documents.",
    );

    await ask(page, "dropped case");
    await expect(alertText(page).first()).toContainText("Cannot reach the service.");
    await expect(page.getByLabel("Your question")).toHaveValue("dropped case");
    // The question text is still there for another go.
    await page.getByLabel("Your question").fill("fine now");
    await page.getByRole("button", { name: "Ask", exact: true }).click();
    await expect(page.getByTestId("answer").first()).toBeVisible();
  });

  test("cancel stops the work on the API (abort propagation) and the question can be asked again", async ({
    page,
  }) => {
    await openAsk(page, await readyUser("cancel"));
    await ask(page, "hang forever");
    await expect(page.getByTestId("stage")).toHaveText("Searching your documents");
    await page.getByRole("button", { name: "Cancel", exact: true }).click();
    await expect(page.getByTestId("cancelled")).toContainText("You cancelled this question.");
    await expect(page.getByRole("button", { name: "Ask", exact: true })).toBeEnabled();
    await expect(page.getByLabel("Your question")).toHaveValue("hang forever");
    await expect
      .poll(async () => (await mockState()).ask.abortedQuestions.includes("hang forever"), {
        timeout: 10_000,
      })
      .toBe(true);
    await expect(page.getByTestId("live-region")).toContainText("The question was cancelled.");
  });

  test("scope: a chosen subset is sent; not-ready documents are not selectable; the documents page links here", async ({
    page,
  }) => {
    const email = await readyUser("scope");
    await signInAndLand(page, email);
    await page.getByRole("link", { name: "Ask about this document" }).nth(1).click(); // second ready row
    await expect(page).toHaveURL(/\/en\/ask\?doc=/);
    await expect(page.getByRole("radio", { name: "Only the documents I choose" })).toBeChecked();
    await expect(page.getByRole("checkbox", { name: /pending\.txt/ })).toBeDisabled();
    const checked = await page.getByRole("checkbox", { checked: true }).count();
    expect(checked).toBe(1);
    await ask(page, "scoped question");
    await expect(page.getByTestId("answer")).toBeVisible();
    const body = (await mockState()).ask.bodies.find((b) => b.question === "scoped question")!;
    expect(body.documentIds).toHaveLength(1);
  });

  test("an empty question is refused with a message; the counter counts", async ({ page }) => {
    await openAsk(page, await readyUser("empty"));
    await page.getByRole("button", { name: "Ask", exact: true }).click();
    await expect(page.getByText("Enter a question.")).toBeVisible();
    await page.getByLabel("Your question").fill("12345");
    await expect(page.getByTestId("counter")).toHaveText("5 / 2000");
    await expect(page.getByLabel("Your question")).toHaveAttribute("maxlength", "2000");
  });

  test("no ready documents: a clear state with the way to the documents page; unconfirmed: the reason", async ({
    page,
  }) => {
    const email = await newUser("none");
    await signInAndLand(page, email);
    await page.goto("/en/ask");
    await expect(page.getByTestId("no-ready")).toContainText("no documents that are ready yet");
    await page.getByRole("link", { name: "Go to your documents" }).click();
    await expect(page).toHaveURL(/\/en\/documents$/);

    const other = await newUser("unconf");
    await seedDocuments(other, []);
    await page.context().clearCookies();
    await signInAndLand(page, "unconfirmed@example.com");
    await page.goto("/en/ask");
    await expect(page.getByTestId("unconfirmed")).toBeVisible();
  });

  test("anonymous visitors are sent to sign-in and come back", async ({ page }) => {
    await page.goto("/en/ask");
    await expect(page).toHaveURL(/\/en\/sign-in\?next=%2Fen%2Fask/);
  });
});

test.describe("ask page in Arabic and accessibility", () => {
  test("Arabic UI: mirrored, Arabic stage and answer labels", async ({ page }) => {
    await openAsk(page, await readyUser("ar"), "ar");
    await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
    await expect(page.getByRole("heading", { level: 1 })).toHaveText("اسأل");
    await page.getByLabel("سؤالك").fill("slow سؤال");
    await page.getByRole("button", { name: "اسأل", exact: true }).click();
    await expect(page.getByTestId("stage")).toHaveText("جارٍ البحث في مستنداتك");
    await expect(page.getByTestId("answer")).toBeVisible();
    await expect(page.getByTestId("answer-meta")).toContainText("تمت الإجابة بمفتاحك الخاص");
    const text = await page.locator("main").innerText();
    expect(text).not.toMatch(/[٠-٩]/);
  });

  for (const locale of ["en", "ar"]) {
    for (const scheme of ["light", "dark"] as const) {
      test(`axe: ask page (${locale}, ${scheme}) while working and with an answer`, async ({
        page,
      }) => {
        await page.emulateMedia({ colorScheme: scheme });
        await openAsk(page, await readyUser(`axe${locale}${scheme}`), locale);
        const serious = async () =>
          (await new AxeBuilder({ page }).analyze()).violations.filter(
            (v) => v.impact === "serious" || v.impact === "critical",
          );
        expect(await serious()).toEqual([]);
        await page.getByLabel(/Your question|سؤالك/).fill("slow notice please");
        await page.getByRole("button", { name: /^(Ask|اسأل)$/ }).click();
        await expect(page.getByTestId("progress")).toBeVisible();
        expect(await serious()).toEqual([]);
        await expect(page.getByTestId("answer")).toBeVisible();
        expect(await serious()).toEqual([]);
      });
    }
  }

  test("reflows at 320 px with a long answer and long file names", async ({ page }) => {
    await page.setViewportSize({ width: 320, height: 700 });
    await openAsk(page, await readyUser("narrow"));
    await ask(page, "long answer please");
    await expect(page.getByTestId("answer")).toBeVisible();
    const overflow = await page.evaluate(
      () => document.documentElement.scrollWidth - document.documentElement.clientWidth,
    );
    expect(overflow).toBeLessThanOrEqual(0);
  });
});
