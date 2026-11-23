import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import { mockState, newUser, seedDocuments, setDemo, signInAndLand } from "./helpers";

async function readyUser(prefix: string) {
  const email = await newUser(prefix);
  await seedDocuments(email, [
    { fileName: "lease.pdf", status: "Ready" },
    { fileName: "addendum.docx", status: "Ready", extractionNotice: "Arabic text was repaired." },
    { fileName: "عقد.md", status: "Ready" },
    { fileName: "pending.txt", status: "Pending" },
  ]);
  return email;
}

const open = async (page: Page, email: string, path: string) => {
  await signInAndLand(page, email);
  await page.goto(path);
};

test.describe("summary (UI-SUM-1, 3, 4)", () => {
  test("from a document row: preselected, optional focus, working state, cited result; the browser sends no token", async ({
    page,
  }) => {
    const email = await readyUser("sum");
    await signInAndLand(page, email);
    const requests: Record<string, string>[] = [];
    page.on("request", async (r) => {
      if (r.url().includes("/summary")) requests.push(await r.allHeaders());
    });
    await page
      .getByTestId("doc-row")
      .filter({ hasText: "lease.pdf" })
      .getByRole("link", { name: "Summarize" })
      .click();
    await expect(page).toHaveURL(/\/en\/summary\?doc=/);
    await expect(page.getByRole("heading", { level: 1 })).toHaveText("Summarize a document");
    await expect(page.getByLabel("Document")).toHaveText(/lease\.pdf/);
    await expect(page.getByLabel("Document").locator("option:checked")).toHaveText("lease.pdf");
    // Only Ready documents are offered.
    await expect(
      page.getByLabel("Document").locator("option", { hasText: "pending.txt" }),
    ).toHaveCount(0);

    await page.getByLabel(/Focus/).fill("slow: payment terms");
    await expect(page.getByTestId("focus-count")).toHaveText("19 / 500");
    await page.getByRole("button", { name: "Summarize" }).click();
    await expect(page.getByTestId("working")).toContainText("Working on it");
    await expect(page.getByTestId("working")).toContainText("up to two minutes");
    await expect(page.getByRole("button", { name: "Cancel" })).toBeVisible();

    const result = page.getByTestId("result");
    await expect(result).toContainText("lease.pdf sets out the rent");
    await expect(result.getByTestId("citation")).toHaveCount(2);
    await expect(result.getByTestId("answer-meta")).toContainText("Answered with your own key");
    await expect(page.getByTestId("live-region")).toContainText("The summary is ready.");
    await result.getByTestId("citation-marker").first().click();
    await expect(page.locator("#summary-cite-1")).toBeFocused();

    expect(requests[0]!.authorization).toBeUndefined();
    const call = (await mockState()).workflows.find((w) => w.focus === "slow: payment terms")!;
    expect(call).toMatchObject({ kind: "summary", auth: "bearer" });
  });

  test("the demo cost note shows before submitting; at zero the button is off and the reset time and the way to add a key are shown", async ({
    page,
  }) => {
    const email = await readyUser("sumdemo");
    await setDemo(email, { used: 9, limit: 10 });
    await open(page, email, "/en/summary");
    await expect(page.getByTestId("demo-cost")).toContainText(
      "counts as one of your daily questions",
    );
    await page.getByLabel("Document").selectOption({ label: "lease.pdf" });
    await page.getByRole("button", { name: "Summarize" }).click();
    await expect(page.getByTestId("result")).toBeVisible(); // the last question

    await page.goto("/en/summary");
    await expect(page.getByTestId("problem-alert")).toContainText(
      "You have used today's demo questions. They reset at",
    );
    await expect(
      page.getByTestId("problem-alert").getByRole("link", { name: "Add an API key" }),
    ).toBeVisible();
    await expect(page.getByRole("button", { name: "Summarize" })).toBeDisabled();
  });

  test("a stale page still gets the API's quota message with the reset time", async ({ page }) => {
    const email = await readyUser("sumstale");
    await setDemo(email, { used: 2, limit: 10 });
    await open(page, email, "/en/summary");
    await setDemo(email, { used: 10, limit: 10 }); // used up in another tab
    await page.getByLabel("Document").selectOption({ label: "lease.pdf" });
    await page.getByRole("button", { name: "Summarize" }).click();
    await expect(page.getByTestId("problem-alert")).toContainText(
      "You have used today's demo questions. They reset at",
    );
  });

  test("no demo note for an account with its own key", async ({ page }) => {
    const email = await readyUser("sumkey");
    await seedDocuments(email, [{ fileName: "a.pdf", status: "Ready" }], { hasKey: true });
    await open(page, email, "/en/summary");
    await expect(page.getByTestId("workflow-form")).toBeVisible();
    await expect(page.getByTestId("demo-cost")).toHaveCount(0);
  });

  test("cancel aborts the call to the API and nothing is shown afterwards", async ({ page }) => {
    const email = await readyUser("sumcancel");
    await open(page, email, "/en/summary");
    await page.getByLabel("Document").selectOption({ label: "lease.pdf" });
    await page.getByLabel(/Focus/).fill("hang forever");
    await page.getByRole("button", { name: "Summarize" }).click();
    await expect(page.getByTestId("working")).toBeVisible();
    await page.getByRole("button", { name: "Cancel" }).click();
    await expect(page.getByTestId("cancelled")).toContainText("You cancelled this request.");
    await expect(page.getByTestId("result")).toHaveCount(0);
    await expect
      .poll(
        async () => (await mockState()).ask.abortedQuestions.includes("workflow:hang forever"),
        { timeout: 10_000 },
      )
      .toBe(true);
  });

  test("errors: 404, 409, 504 and the LLM codes each show their message; retry works", async ({
    page,
  }) => {
    const email = await readyUser("sumerr");
    await open(page, email, "/en/summary");
    const docs = [
      { fileName: "lease.pdf", status: "Ready" as const },
      { fileName: "addendum.docx", status: "Ready" as const },
    ];
    const submit = async (focus: string) => {
      await page.getByLabel(/Focus/).fill(focus);
      await page.getByRole("button", { name: "Summarize" }).click();
    };
    await page.getByLabel("Document").selectOption({ label: "lease.pdf" });

    await submit("timeout please");
    await expect(page.getByTestId("problem-alert")).toContainText(
      "The question took too long. Try a narrower question or fewer documents.",
    );
    await submit("nokey please");
    await expect(page.getByTestId("problem-alert")).toContainText(
      "Add an API key in Settings to ask questions.",
    );
    await submit("nocredit please");
    await expect(page.getByTestId("problem-alert")).toContainText("has no credit left");
    await submit("unavailable please");
    await expect(page.getByTestId("problem-alert")).toContainText(
      "The model provider is not responding.",
    );

    await seedDocuments(email, [{ ...docs[1]! }]); // lease.pdf is gone
    await submit("anything");
    await expect(page.getByTestId("problem-alert")).toContainText(
      "This document no longer exists.",
    );
    await seedDocuments(email, [{ fileName: "lease.pdf", status: "Pending" }, docs[1]!]);
    // The page's document ids are the seeded ones from before; re-seeding made new ids, so this too is "gone".
    await submit("anything");
    await expect(page.getByTestId("problem-alert")).toContainText(
      "This document no longer exists.",
    );
  });

  test("a document that stops being ready gives the 'not ready' message (409)", async ({
    page,
  }) => {
    const email = await newUser("sum409");
    await seedDocuments(email, [{ fileName: "a.pdf", status: "Ready" }]);
    await open(page, email, "/en/summary");
    // Make the same document (same id) not ready: the mock keeps ids, so flip its status through a second seed with the id.
    const state = await page.evaluate(async () => (await fetch("/api/proxy/documents")).json());
    const id = (state as { id: string }[])[0]!.id;
    await seedDocuments(email, [{ fileName: "a.pdf", status: "Processing", id }]);
    await page.getByLabel("Document").selectOption({ label: "a.pdf" });
    await page.getByRole("button", { name: "Summarize" }).click();
    await expect(page.getByTestId("problem-alert")).toContainText(
      "This document is not ready yet.",
    );
  });

  test("a summary uses one demo question: the allowance in /me goes down", async ({ page }) => {
    const email = await readyUser("sumcount");
    await setDemo(email, { used: 2, limit: 10 });
    await open(page, email, "/en/summary");
    await page.getByLabel("Document").selectOption({ label: "lease.pdf" });
    await page.getByRole("button", { name: "Summarize" }).click();
    await expect(page.getByTestId("result")).toBeVisible();
    await expect(page.getByTestId("answer-notices")).toContainText("Demo mode:");
    const me = await page.evaluate(async () => (await fetch("/api/proxy/me")).json());
    expect((me as { demo: { used: number; remaining: number } }).demo).toMatchObject({
      used: 3,
      remaining: 7,
    });
  });
});

test.describe("compare (UI-SUM-2)", () => {
  test("from a row: first document preselected, the same document cannot be chosen twice, cited result for both", async ({
    page,
  }) => {
    const email = await readyUser("cmp");
    await signInAndLand(page, email);
    await page
      .getByTestId("doc-row")
      .filter({ hasText: "lease.pdf" })
      .getByRole("link", { name: "Compare with another" })
      .click();
    await expect(page).toHaveURL(/\/en\/compare\?a=/);
    const first = page.getByLabel("First document");
    const second = page.getByLabel("Second document");
    await expect(first.locator("option:checked")).toHaveText("lease.pdf");
    await expect(second.locator("option", { hasText: "lease.pdf" })).toBeDisabled();

    await page.getByRole("button", { name: "Compare", exact: true }).click();
    await expect(page.getByTestId("form-error")).toHaveText("Choose two documents.");

    await second.selectOption({ label: "عقد.md" });
    await expect(first.locator("option", { hasText: "عقد.md" })).toBeDisabled();
    await page.getByLabel(/Focus/).fill("notice period");
    await page.getByRole("button", { name: "Compare", exact: true }).click();
    const result = page.getByTestId("result");
    await expect(result).toContainText("Both documents cover rent");
    await expect(result).toContainText("lease.pdf");
    await expect(result).toContainText("عقد.md");
    await expect(result.getByTestId("citation")).toHaveCount(2);
    const call = (await mockState()).workflows.find((w) => w.focus === "notice period")!;
    expect(call).toMatchObject({ kind: "compare", auth: "bearer" });
    expect(call.ids).toHaveLength(2);
    expect(new Set(call.ids).size).toBe(2);
  });

  test("a notice of a compared document shows with the result; the demo note precedes the form", async ({
    page,
  }) => {
    const email = await readyUser("cmpnotice");
    await setDemo(email, { used: 0, limit: 10 });
    await open(page, email, "/en/compare");
    await expect(page.getByTestId("demo-cost")).toBeVisible();
    await page.getByLabel("First document").selectOption({ label: "lease.pdf" });
    await page.getByLabel("Second document").selectOption({ label: "addendum.docx" });
    await page.getByRole("button", { name: "Compare", exact: true }).click();
    await expect(page.getByTestId("answer-notices")).toContainText(
      "addendum.docx: Arabic text was repaired.",
    );
    await expect(page.getByTestId("answer-notices")).toContainText("Demo mode:");
  });

  test("needs two ready documents", async ({ page }) => {
    const email = await newUser("cmpone");
    await seedDocuments(email, [{ fileName: "only.pdf", status: "Ready" }]);
    await open(page, email, "/en/compare");
    await expect(page.getByTestId("not-enough")).toContainText("at least two documents");
  });

  test("Arabic UI: labels, result and mirrored layout", async ({ page }) => {
    const email = await readyUser("cmpar");
    await open(page, email, "/ar/compare");
    await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
    await expect(page.getByRole("heading", { level: 1 })).toHaveText("مقارنة مستندين");
    await page.getByLabel("المستند الأول").selectOption({ label: "lease.pdf" });
    await page.getByLabel("المستند الثاني").selectOption({ label: "عقد.md" });
    await page.getByRole("button", { name: "قارن", exact: true }).click();
    await expect(page.getByTestId("result")).toBeVisible();
    await expect(page.getByRole("heading", { level: 2, name: "المقارنة" })).toBeVisible();
  });
});

test.describe("summary and compare: accessibility", () => {
  for (const locale of ["en", "ar"]) {
    for (const scheme of ["light", "dark"] as const) {
      test(`axe: summary and compare (${locale}, ${scheme}) idle, working and with a result`, async ({
        page,
      }) => {
        await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });
        const email = await readyUser(`axe${locale}${scheme}`);
        await setDemo(email, { used: 1, limit: 10 });
        await signInAndLand(page, email);
        const serious = async () =>
          (await new AxeBuilder({ page }).analyze()).violations.filter(
            (v) => v.impact === "serious" || v.impact === "critical",
          );

        await page.goto(`/${locale}/summary`);
        expect(await serious()).toEqual([]);
        await page.locator("#doc-a").selectOption({ index: 1 });
        await page.locator("#focus").fill("slow");
        await page.getByTestId("workflow-form").locator("button[type=submit]").click();
        await expect(page.getByTestId("working")).toBeVisible();
        expect(await serious()).toEqual([]);
        await expect(page.getByTestId("result")).toBeVisible();
        expect(await serious()).toEqual([]);

        await page.goto(`/${locale}/compare`);
        await page.locator("#doc-a").selectOption({ index: 1 });
        await page.locator("#doc-b").selectOption({ index: 2 });
        await page.getByTestId("workflow-form").locator("button[type=submit]").click();
        await expect(page.getByTestId("result")).toBeVisible();
        expect(await serious()).toEqual([]);
      });
    }
  }

  test("reflows at 320 px", async ({ page }) => {
    await page.setViewportSize({ width: 320, height: 700 });
    const email = await readyUser("narrow");
    await open(page, email, "/en/compare");
    await page.getByLabel("First document").selectOption({ label: "lease.pdf" });
    await page.getByLabel("Second document").selectOption({ label: "عقد.md" });
    await page.getByRole("button", { name: "Compare", exact: true }).click();
    await expect(page.getByTestId("result")).toBeVisible();
    const overflow = await page.evaluate(
      () => document.documentElement.scrollWidth - document.documentElement.clientWidth,
    );
    expect(overflow).toBeLessThanOrEqual(0);
  });
});
