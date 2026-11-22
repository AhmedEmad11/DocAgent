import { expect, test } from "@playwright/test";
import { mock, mockState } from "./helpers";

// The mock's external-login outcome is global state, so these tests run one after another.
test.describe.configure({ mode: "serial" });

test.beforeEach(async () => {
  await mock("/__test/external", { outcome: "ok" });
});

test("Continue with Google: nonce, exchange on the server, code gone from the URL (UI-AUTH-6, UI-SEC-3, 5)", async ({
  page,
}) => {
  const callbacks: { url: string; status: number; headers: Record<string, string> }[] = [];
  page.on("response", (r) => {
    if (r.url().includes("/api/auth/external/callback")) {
      callbacks.push({ url: r.url(), status: r.status(), headers: r.headers() });
    }
  });

  await page.goto("/en/sign-in");
  await page.getByRole("link", { name: "Continue with Google" }).click();
  await expect(page).toHaveURL(/\/en\/documents$/);
  await expect(page.getByTestId("account-email")).toHaveText("social@example.com");
  await expect(page.getByTestId("account-methods")).toHaveText("Google");
  expect(page.url()).not.toMatch(/code=|[?&]s=/);

  const cb = callbacks[0]!;
  expect(cb.url).toMatch(/[?&]s=[\w-]{40,}/); // the nonce rode in returnUrl
  expect(cb.url).toMatch(/[?&]code=EXT-/);
  expect(cb.status).toBe(303); // answered with a redirect
  expect(cb.headers["cache-control"]).toBe("no-store");
  expect(cb.headers["referrer-policy"]).toBe("no-referrer");

  // The code is single use and the nonce cookie is gone: replaying the callback URL fails (UI-SEC-5).
  await page.context().clearCookies({ name: /docagent_session/ });
  await page.goto(cb.url);
  await expect(page).toHaveURL(/\/en\/sign-in\?error=external_login_failed$/);
  await expect(page.locator("main [role=alert]")).toContainText(
    "Signing in with Google or GitHub did not work",
  );
});

test("Continue with GitHub works and keeps the page to return to", async ({ page }) => {
  await page.goto("/en/sign-in?next=%2Far%2Fprivacy");
  await page.getByRole("link", { name: "Continue with GitHub" }).click();
  await expect(page).toHaveURL(/\/ar\/privacy$/);
});

for (const [outcome, text] of [
  ["email_not_verified", "has not verified your email address"],
  ["no_verified_email", "did not share a verified email address"],
  ["external_login_failed", "did not work"],
  ["something_unexpected", "did not work"], // unknown values fall back, nothing is echoed
  ["bad_code", "did not work"], // the exchange refuses the code
] as const) {
  test(`?error=${outcome} shows a clear message with the way back to sign-in (en)`, async ({
    page,
  }) => {
    await mock("/__test/external", { outcome });
    await page.goto("/en/sign-in");
    await page.getByRole("link", { name: "Continue with Google" }).click();
    await expect(page).toHaveURL(/\/en\/sign-in\?error=/);
    await expect(page.locator("main [role=alert]")).toContainText(text);
    await expect(page.getByRole("button", { name: "Sign in" })).toBeVisible();
    expect(await page.content()).not.toContain("something_unexpected");
  });
}

test("the error message is shown in Arabic", async ({ page }) => {
  await mock("/__test/external", { outcome: "email_not_verified" });
  await page.goto("/ar/sign-in");
  await page.getByRole("link", { name: "المتابعة باستخدام Google" }).click();
  await expect(page).toHaveURL(/\/ar\/sign-in\?error=email_not_verified$/);
  await expect(page.locator("main [role=alert]")).toContainText("لم تتحقق");
});

test("a callback without the nonce cookie is refused before any exchange (login CSRF)", async ({
  request,
}) => {
  await mock("/__test/external", { outcome: "ok" });
  const before = (await mockState()).lastExchangeCode;
  const res = await request.get("/api/auth/external/callback?s=forged&code=EXT-forged", {
    maxRedirects: 0,
  });
  expect(res.status()).toBe(303);
  expect(res.headers()["location"]).toContain("/en/sign-in?error=external_login_failed");
  expect((await mockState()).lastExchangeCode).toBe(before); // the code was never sent to the API
  expect(res.headers()["set-cookie"] ?? "").not.toContain("docagent_session=");
});

test("a callback with a nonce that does not match the browser's cookie is refused", async ({
  page,
}) => {
  await page.goto("/api/auth/external/google"); // starts a flow: cookie set, mock redirects back with the real nonce
  await expect(page).toHaveURL(/\/en\/documents$/);
  await page.context().clearCookies({ name: /docagent_session/ });
  const before = (await mockState()).lastExchangeCode;
  await page.goto("/api/auth/external/callback?s=another-nonce&code=EXT-forged");
  await expect(page).toHaveURL(/error=external_login_failed/);
  expect((await mockState()).lastExchangeCode).toBe(before);
});

test("unknown providers are not found", async ({ request }) => {
  const res = await request.get("/api/auth/external/evil", { maxRedirects: 0 });
  expect(res.status()).toBe(404);
});
