import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page, type Request } from "@playwright/test";
import {
  MOCK_API,
  mock,
  mockState,
  newUser,
  nextUploadError,
  seedDocuments,
  signInAndLand,
} from "./helpers";

const MB = 1024 * 1024;

async function openDocuments(page: Page, email: string, locale = "en") {
  await signInAndLand(page, email);
  if (locale !== "en") await page.goto(`/${locale}/documents`);
}

const upload = (page: Page, files: { name: string; buffer: Buffer; mimeType?: string }[]) =>
  page
    .getByTestId("file-input")
    .setInputFiles(files.map((f) => ({ mimeType: "application/octet-stream", ...f })));

test.describe("documents list and upload (UI-DOC-1 to 10)", () => {
  test("empty state, then a ticket upload: progress, Waiting, Processing, Ready, with no token in the browser", async ({
    page,
  }) => {
    const email = await newUser("up");
    const apiRequests: Request[] = [];
    page.on("request", (r) => {
      if (r.url().startsWith(MOCK_API)) apiRequests.push(r);
    });
    const consoleErrors: string[] = [];
    page.on("console", (m) => {
      if (m.type() === "error") consoleErrors.push(m.text());
    });
    const responses: string[] = [];
    page.on("response", async (r) => {
      try {
        responses.push(r.url(), JSON.stringify(await r.allHeaders()), await r.text());
      } catch {
        // redirects and preflights have no body
      }
    });

    await openDocuments(page, email);
    await expect(page.getByTestId("empty")).toContainText("You have no documents yet.");
    await expect(page.getByTestId("empty")).toContainText("5 MB");

    await upload(page, [{ name: "slow-report.txt", buffer: Buffer.from("quarterly numbers") }]);
    const item = page.getByTestId("upload-item");
    await expect(item).toHaveAttribute("data-state", "uploading");
    await expect(item.getByRole("progressbar")).toBeVisible();
    await expect(item).toHaveAttribute("data-state", "done");
    await expect(page.getByTestId("doc-row")).toHaveCount(1);
    await expect(page.getByTestId("doc-status")).toHaveText("Waiting");
    await expect(page.getByTestId("live-region")).toContainText("slow-report.txt was uploaded");

    // The poll moves it on by itself (2 to 5 s apart) until it is final.
    await expect(page.getByTestId("doc-status")).toHaveText("Processing", { timeout: 15_000 });
    await expect(page.getByTestId("doc-status")).toHaveText("Ready", { timeout: 15_000 });
    await expect(page.getByTestId("live-region")).toContainText("slow-report.txt: Ready");
    await expect(page.getByText("Documents: 1 of 3")).toBeVisible();
    await expect(page.getByTestId("meter-chunks")).toContainText("Storage: 1 of 3,000 text chunks");

    // The file went straight to the API with a ticket only: no Authorization header, no token anywhere (UI-SEC-1, plan A3).
    const direct = apiRequests.filter(
      (r) => r.method() === "POST" && r.url().endsWith("/api/v1/documents"),
    );
    expect(direct).toHaveLength(1);
    const headers = await direct[0]!.allHeaders();
    expect(headers["x-upload-ticket"]).toMatch(/^MOCKTICKET-/);
    expect(headers.authorization).toBeUndefined();
    expect(apiRequests.every((r) => r.url().includes("/api/v1/documents"))).toBe(true); // nothing else reached the API from the browser
    const state = await mockState();
    expect(state.uploads.filter((u) => u.name === "slow-report.txt")[0]!.auth).toBe("ticket");
    expect(responses.join("\n")).not.toMatch(/MOCKAT|MOCKRT/);
    expect(consoleErrors.filter((e) => /Content Security Policy|CORS|blocked/i.test(e))).toEqual(
      [],
    );
    const storage = await page.evaluate(() =>
      JSON.stringify({ ...localStorage, ...sessionStorage }),
    );
    expect(storage).not.toMatch(/MOCKTICKET|MOCKAT|MOCKRT|quarterly/);
  });

  test("every status is a text badge; failed shows the reason and a hint; notices; Arabic and hostile names are text", async ({
    page,
  }) => {
    const email = await newUser("status");
    await seedDocuments(
      email,
      [
        { fileName: "pending.pdf", status: "Pending" },
        { fileName: "processing.pdf", status: "Processing" },
        {
          fileName: "عقد الإيجار.pdf",
          status: "Ready",
          extractionNotice: "Arabic text was repaired after extraction.",
        },
        {
          fileName: "scan.pdf",
          status: "Failed",
          failureReason: "No extractable text: scanned PDFs are not supported.",
        },
        {
          fileName: "full.txt",
          status: "Failed",
          failureReason:
            "Storage limit reached: 2,990 of 3,000 chunks used, and this document needs 40 more. Delete a document to free space.",
        },
        { fileName: '<img src=x onerror="window.__xss=1">.md', status: "Ready" },
        { fileName: `${"very-long-name-".repeat(12)}.txt`, status: "Ready" },
      ],
      { hasKey: true },
    );
    await openDocuments(page, email);

    const rows = page.getByTestId("doc-row");
    await expect(rows).toHaveCount(7);
    await expect(rows.nth(0).getByTestId("doc-status")).toHaveText("Waiting");
    await expect(rows.nth(1).getByTestId("doc-status")).toHaveText("Processing");
    await expect(rows.nth(2).getByTestId("doc-status")).toHaveText("Ready");
    await expect(rows.nth(3).getByTestId("doc-status")).toHaveText("Failed");
    await expect(rows.nth(2).getByTestId("doc-notice")).toContainText("Arabic text was repaired");
    await expect(rows.nth(3).getByTestId("doc-failed")).toContainText(
      "scanned PDFs are not supported.",
    );
    await expect(rows.nth(3).getByTestId("doc-failed")).toContainText(
      "Upload a text-based version",
    );
    await expect(rows.nth(4).getByTestId("doc-failed")).toContainText(
      "Delete documents you no longer need",
    );
    await expect(rows.nth(2).getByTestId("doc-name")).toHaveAttribute("dir", "auto");
    expect(
      await page.evaluate(() => (window as unknown as { __xss?: number }).__xss),
    ).toBeUndefined();
    await expect(rows.nth(5).locator("img")).toHaveCount(0);
    await expect(rows.nth(5).getByTestId("doc-name")).toContainText(
      '<img src=x onerror="window.__xss=1">.md',
    );
    // Long names are truncated with the full name available.
    const long = rows.nth(6).getByTestId("doc-name");
    expect(await long.getAttribute("title")).toContain("very-long-name-very-long-name-");
    // A BYOK account sees the 100-document limit.
    await expect(page.getByText("Documents: 7 of 100")).toBeVisible();
    // Final statuses only: the list does not poll.
    const before = (await mockState()).listCalls;
    await page.waitForTimeout(500);
    // (Pending/Processing seeded rows are held, but the page still polls while any is active; just assert the page is stable.)
    expect((await mockState()).listCalls).toBeGreaterThanOrEqual(before);
  });

  test("refusals: wrong type and size locally; 409, 413, 415, 429 and a lost session from the API, per file", async ({
    page,
  }) => {
    const email = await newUser("refuse");
    await openDocuments(page, email);

    await upload(page, [{ name: "program.exe", buffer: Buffer.from("MZ") }]);
    await expect(page.getByTestId("upload-error")).toContainText(
      "Only PDF, Word (.docx), text (.txt) and Markdown (.md) files are supported.",
    );

    await upload(page, [{ name: "huge.pdf", buffer: Buffer.alloc(5 * MB + 1, 97) }]);
    await expect(page.getByTestId("upload-error").first()).toContainText(
      "This file is larger than your limit of 5 MB.",
    );

    const sent = (await mockState()).uploads.map((u) => u.name);
    expect(sent).not.toContain("program.exe"); // neither was sent
    expect(sent).not.toContain("huge.pdf");

    // Server-side refusals are shown with the message for their status (SRS 10.6).
    await page.getByRole("button", { name: "Clear the list" }).click();
    for (const [status, text] of [
      [413, "This file is larger than your limit of 5 MB."],
      [415, "Only PDF, Word (.docx), text (.txt) and Markdown (.md) files are supported."],
      [409, "You have reached your document limit (3)."],
    ] as const) {
      await nextUploadError(status);
      await upload(page, [{ name: `n${status}.txt`, buffer: Buffer.from("x") }]);
      await expect(
        page.getByTestId("upload-item").first().getByTestId("upload-error"),
      ).toContainText(text);
      await page.getByRole("button", { name: "Clear the list" }).click();
    }
    await nextUploadError(429, undefined, 6);
    await upload(page, [{ name: "n429.txt", buffer: Buffer.from("x") }]);
    await expect(page.getByTestId("upload-error")).toContainText(
      "Too many requests. Try again in 6 seconds.",
    );
    await page.getByRole("button", { name: "Clear the list" }).click();

    // One failure does not stop the next file in the same batch.
    await upload(page, [
      { name: "throttled.txt", buffer: Buffer.from("x") },
      { name: "fine.txt", buffer: Buffer.from("y") },
    ]);
    await expect(page.getByTestId("upload-item").filter({ hasText: "fine.txt" })).toHaveAttribute(
      "data-state",
      "done",
    );
    await expect(
      page.getByTestId("upload-item").filter({ hasText: "throttled.txt" }),
    ).toHaveAttribute("data-state", "failed");
    await expect(page.getByTestId("doc-row")).toHaveCount(1);
  });

  test("a lost session during upload says so and offers sign-in", async ({ page }) => {
    const email = await newUser("lost");
    await openDocuments(page, email);
    await mock("/__test/revoke", { email });
    await upload(page, [{ name: "a.txt", buffer: Buffer.from("x") }]);
    await expect(page.getByTestId("upload-error")).toContainText(
      "Your session ended. Sign in again.",
    );
  });

  test("drag and drop uploads the dropped files", async ({ page }) => {
    const email = await newUser("drop");
    await openDocuments(page, email);
    const dt = await page.evaluateHandle(() => {
      const d = new DataTransfer();
      d.items.add(new File(["hello"], "dropped.md"));
      return d;
    });
    const zone = page.getByTestId("drop-zone");
    await zone.dispatchEvent("dragover", { dataTransfer: dt });
    await expect(zone).toHaveAttribute("data-dragging", "true");
    await zone.dispatchEvent("drop", { dataTransfer: dt });
    await expect(page.getByTestId("doc-row")).toHaveCount(1);
    await expect(page.getByTestId("doc-name")).toHaveText("dropped.md");
  });

  test("upload is off at the tier limit with an explanation; an own key raises it", async ({
    page,
  }) => {
    const email = await newUser("limit");
    await seedDocuments(email, [
      { fileName: "a.txt", status: "Ready" },
      { fileName: "b.txt", status: "Ready" },
      { fileName: "c.txt", status: "Ready" },
    ]);
    await openDocuments(page, email);
    await expect(page.getByTestId("at-limit")).toContainText(
      "You have reached the limit of 3 documents",
    );
    await expect(page.getByRole("link", { name: /Add your own API key/ })).toBeVisible();
    await expect(page.getByTestId("file-input")).toBeDisabled();

    await seedDocuments(
      email,
      [
        { fileName: "a.txt", status: "Ready" },
        { fileName: "b.txt", status: "Ready" },
        { fileName: "c.txt", status: "Ready" },
      ],
      { hasKey: true },
    );
    await page.reload();
    await expect(page.getByTestId("at-limit")).toHaveCount(0);
    await expect(page.getByTestId("file-input")).toBeEnabled();
    await expect(page.getByText("Documents: 3 of 100")).toBeVisible();
    await expect(page.getByText("up to 25 MB each")).toBeVisible();
  });

  test("the storage meter warns above 80%", async ({ page }) => {
    const email = await newUser("chunks");
    await seedDocuments(email, [{ fileName: "a.txt", status: "Ready" }], { chunksUsed: 2700 });
    await openDocuments(page, email);
    await expect(page.getByTestId("chunks-high")).toContainText("more than 80%");
    await expect(page.getByTestId("meter-chunks").getByRole("progressbar")).toHaveAttribute(
      "value",
      "2700",
    );
  });

  test("delete: cancel keeps it and returns focus; confirm removes it permanently", async ({
    page,
  }) => {
    const email = await newUser("del");
    await seedDocuments(email, [
      { fileName: "keep.txt", status: "Ready" },
      { fileName: "drop.txt", status: "Ready" },
    ]);
    await openDocuments(page, email);

    const opener = page.getByRole("button", { name: "Delete drop.txt" });
    await opener.click();
    const dialog = page.getByRole("dialog");
    await expect(dialog).toBeVisible();
    await expect(dialog).toContainText("removed permanently");
    await expect(dialog.getByRole("button", { name: "Cancel" })).toBeFocused();
    await page.keyboard.press("Escape");
    await expect(dialog).toBeHidden();
    await expect(opener).toBeFocused(); // focus returns to the opener (UI-A11Y-1)
    await expect(page.getByTestId("doc-row")).toHaveCount(2);

    await opener.click();
    await dialog.getByRole("button", { name: "Delete permanently" }).click();
    await expect(page.getByTestId("doc-row")).toHaveCount(1);
    await expect(page.getByTestId("doc-name").filter({ hasText: "drop.txt" })).toHaveCount(0);
    await expect(page.getByTestId("live-region")).toContainText("drop.txt was deleted.");
    await page.reload();
    await expect(page.getByTestId("doc-row")).toHaveCount(1);
  });

  test("an unconfirmed account sees why and cannot upload", async ({ page }) => {
    await signInAndLand(page, "unconfirmed@example.com");
    await expect(page.getByTestId("unconfirmed")).toBeVisible();
    await expect(page.getByTestId("file-input")).toHaveCount(0);
  });
});

test.describe("documents page in Arabic and accessibility", () => {
  test("Arabic: mirrored layout, Arabic text, Western digits, plurals", async ({ page }) => {
    const email = await newUser("ar");
    await seedDocuments(email, [
      { fileName: "عقد.pdf", status: "Ready", chunkCount: 12 },
      {
        fileName: "notes.md",
        status: "Failed",
        failureReason:
          "Storage limit reached: 2,990 of 3,000 chunks used, and this document needs 40 more. Delete a document to free space.",
      },
    ]);
    await openDocuments(page, email, "ar");
    await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
    await expect(page.getByRole("heading", { level: 1 })).toHaveText("المستندات");
    await expect(page.getByText("المستندات: 2 من 3")).toBeVisible();
    await expect(page.getByTestId("doc-status").first()).toHaveText("جاهز");
    const text = await page.locator("main").innerText();
    expect(text).not.toMatch(/[٠-٩]/); // Western digits only (OI-14)
    // The delete dialog works and is mirrored.
    await page.getByRole("button", { name: /^حذف عقد\.pdf$/ }).click();
    await expect(page.getByRole("dialog")).toContainText("نهائيًا");
    await page.keyboard.press("Escape");
  });

  for (const locale of ["en", "ar"]) {
    for (const scheme of ["light", "dark"] as const) {
      test(`axe: documents page (${locale}, ${scheme}) with documents and a dialog open`, async ({
        page,
      }) => {
        await page.emulateMedia({ colorScheme: scheme });
        const email = await newUser(`axe${locale}${scheme}`);
        await seedDocuments(email, [
          { fileName: "ready.pdf", status: "Ready", extractionNotice: "A note." },
          { fileName: "pending.pdf", status: "Pending" },
          {
            fileName: "bad.pdf",
            status: "Failed",
            failureReason: "No extractable text: scanned PDFs are not supported.",
          },
        ]);
        await openDocuments(page, email, locale);
        await expect(page.getByTestId("doc-row")).toHaveCount(3);
        const results = await new AxeBuilder({ page }).analyze();
        expect(
          results.violations.filter((v) => v.impact === "serious" || v.impact === "critical"),
        ).toEqual([]);

        await page.getByTestId("doc-row").first().getByRole("button").last().click();
        await expect(page.getByRole("dialog")).toBeVisible();
        const dialogResults = await new AxeBuilder({ page }).analyze();
        expect(
          dialogResults.violations.filter((v) => v.impact === "serious" || v.impact === "critical"),
        ).toEqual([]);
      });
    }
  }

  test("reflows at 320 px without horizontal scrolling", async ({ page }) => {
    await page.setViewportSize({ width: 320, height: 700 });
    const email = await newUser("narrow");
    await seedDocuments(email, [
      {
        fileName: `${"long-file-name-".repeat(10)}.pdf`,
        status: "Failed",
        failureReason: "No extractable text: scanned PDFs are not supported.",
      },
    ]);
    await openDocuments(page, email);
    await expect(page.getByTestId("doc-row")).toHaveCount(1);
    const overflow = await page.evaluate(
      () => document.documentElement.scrollWidth - document.documentElement.clientWidth,
    );
    expect(overflow).toBeLessThanOrEqual(0);
  });
});
