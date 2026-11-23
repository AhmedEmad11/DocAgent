import AxeBuilder from "@axe-core/playwright";
import { expect, test } from "@playwright/test";
import {
  PASSWORD,
  mailbox,
  mock,
  mockState,
  newUser,
  signIn,
  signInAndLand,
  uniqueEmail,
} from "./helpers";

test.describe("register, confirm, sign in", () => {
  test("register shows the password rules, then check-email; the emailed link confirms (POST), then the app unlocks", async ({
    page,
  }) => {
    const email = uniqueEmail();
    await page.goto("/en/register");
    await expect(page.getByRole("heading", { level: 1 })).toHaveText("Create your account");
    // The rules are listed before submit (UI-AUTH-1).
    await expect(page.getByText("At least 6 characters")).toBeVisible();

    await page.getByLabel("Email").fill(email);
    await page.getByLabel("Password").fill("abc");
    await page.getByRole("button", { name: "Create account" }).click();
    await expect(page.getByText("The password needs an uppercase letter.").first()).toBeVisible();
    await expect(page.getByText("The password needs a digit.").first()).toBeVisible();
    await expect(page.getByText("The password needs at least 6 characters.").first()).toBeVisible();

    await page.getByLabel("Password").fill(PASSWORD);
    await page.getByRole("button", { name: "Create account" }).click();
    await expect(page).toHaveURL(/\/en\/check-email$/);
    await expect(page.getByText(email)).toBeVisible();
    await expect(page.url()).not.toContain(email); // never in the URL

    // Resend (UI-AUTH-2): the address is remembered, so one button.
    const before = (await mailbox(email)).confirm!;
    await page.getByRole("button", { name: "Resend confirmation email" }).click();
    await expect(page.getByText(/we sent a new link/)).toBeVisible();
    const after = (await mailbox(email)).confirm!;
    expect(after.code).not.toBe(before.code);

    // Signing in before confirming works but the account is flagged (UI-AUTH-2, 3).
    await signInAndLand(page, email);
    await expect(page.getByTestId("unconfirmed")).toBeVisible();

    // The emailed link opens the UI page (plan A5). Opening it changes nothing; the button confirms.
    await page.goto(`/en/confirm?userId=${after.userId}&code=${after.code}`);
    await expect(page.getByRole("heading", { level: 1 })).toHaveText("Confirm your email");
    await page.getByRole("button", { name: "Confirm email" }).click();
    await expect(page.getByText("Your email address is confirmed.")).toBeVisible();
    await page.getByRole("link", { name: "Continue" }).click();
    await expect(page.getByTestId("unconfirmed")).toHaveCount(0);
    await expect(page.getByTestId("empty")).toBeVisible();
  });

  test("a wrong or missing confirmation link says so", async ({ page }) => {
    await page.goto("/en/confirm?userId=nobody&code=wrong");
    await page.getByRole("button", { name: "Confirm email" }).click();
    await expect(page.locator("main [role=alert]")).toContainText("not valid or has expired");
    await page.goto("/en/confirm");
    await expect(page.locator("main [role=alert]")).toContainText("not valid or has expired");
  });

  test("a duplicate email gives the generic message and does not say the address exists", async ({
    page,
  }) => {
    await page.goto("/en/register");
    await page.getByLabel("Email").fill("confirmed@example.com");
    await page.getByLabel("Password").fill(PASSWORD);
    await page.getByRole("button", { name: "Create account" }).click();
    const alert = page.locator("main [role=alert]");
    await expect(alert).toContainText("We could not create the account with these details.");
    await expect(alert.locator("p").first()).not.toContainText(/already|taken|exists/i);
    await expect(alert.getByRole("link", { name: "Already registered? Sign in." })).toBeVisible();
  });

  test("wrong credentials, unknown account and lockout give specific non-leaking messages", async ({
    page,
  }) => {
    await signIn(page, "confirmed@example.com", "Example-Wrong-3");
    const bad = "Email or password is incorrect, or the email is not confirmed yet.";
    await expect(page.locator("main [role=alert]")).toHaveText(bad);
    await signIn(page, uniqueEmail("ghost"), "Example-Wrong-3");
    await expect(page.locator("main [role=alert]")).toHaveText(bad); // same text for an unknown account
    await signIn(page, "locked@example.com");
    await expect(page.locator("main [role=alert]")).toContainText(
      "Too many failed sign-in attempts",
    );
  });

  test("sign-in validates the fields before calling the API", async ({ page }) => {
    await page.goto("/en/sign-in");
    await page.getByRole("button", { name: "Sign in" }).click();
    await expect(page.getByText("Enter your email address.").first()).toBeVisible();
    await expect(page.getByText("Enter a password.").first()).toBeVisible();
    await expect(page.getByLabel("Email")).toHaveAttribute("aria-invalid", "true");
  });

  test("the API's rate limit (429 + Retry-After) is explained, and the visitor IP is forwarded with the secret (A4)", async ({
    browser,
  }) => {
    const ctx = await browser.newContext({
      extraHTTPHeaders: { "x-forwarded-for": "198.51.100.99" },
    });
    const page = await ctx.newPage();
    const email = uniqueEmail("throttled");
    await signIn(page, email);
    await expect(page.locator("main [role=alert]")).toContainText(
      "Too many attempts. Try again in 7 seconds.",
    );
    expect((await mockState()).loginIpByEmail[email]).toBe("198.51.100.99");
    await ctx.close();
  });
});

test.describe("password reset", () => {
  test("forgot shows the same message for known and unknown addresses; the code resets the password", async ({
    page,
  }) => {
    const email = uniqueEmail("reset");
    // A confirmed account to reset: register and confirm through the API pages.
    await page.goto("/en/register");
    await page.getByLabel("Email").fill(email);
    await page.getByLabel("Password").fill(PASSWORD);
    await page.getByRole("button", { name: "Create account" }).click();
    await expect(page).toHaveURL(/check-email/);
    const c = (await mailbox(email)).confirm!;
    await page.goto(`/en/confirm?userId=${c.userId}&code=${c.code}`);
    await page.getByRole("button", { name: "Confirm email" }).click();
    await expect(page.getByText("Your email address is confirmed.")).toBeVisible();

    const sent = "If an account exists for this address, we sent a reset code.";
    for (const address of [uniqueEmail("nobody"), email]) {
      await page.goto("/en/forgot-password");
      await page.getByLabel("Email").fill(address);
      await page.getByRole("button", { name: "Send reset code" }).click();
      await expect(page.locator("main [role=status]")).toContainText(sent);
    }

    const { resetCode } = await mailbox(email);
    await page.getByRole("link", { name: "Enter the reset code" }).click();
    await expect(page).toHaveURL(/\/en\/reset-password$/);
    await page.getByLabel("Email").fill(email);
    await page.getByLabel("Reset code").fill("WRONG-CODE");
    await page.getByLabel("New password").fill("Example-Only-2");
    await page.getByRole("button", { name: "Change password" }).click();
    await expect(page.locator("main [role=alert]")).toContainText(
      "reset code is not valid or has expired",
    );

    await page.getByLabel("Reset code").fill(resetCode!);
    await page.getByRole("button", { name: "Change password" }).click();
    await expect(page).toHaveURL(/\/en\/sign-in\?reset=1$/);
    await expect(page.locator("main [role=status]")).toContainText("Your password was changed");

    await signIn(page, email, PASSWORD);
    await expect(page.locator("main [role=alert]")).toBeVisible(); // the old password no longer works
    await signIn(page, email, "Example-Only-2");
    await expect(page).toHaveURL(/\/en\/documents$/);
  });
});

test.describe("redirects, next and sign-out", () => {
  test("anonymous visitors are sent to sign-in and come back after signing in (UI-AUTH-9)", async ({
    page,
  }) => {
    await page.goto("/en/documents");
    await expect(page).toHaveURL(/\/en\/sign-in\?next=%2Fen%2Fdocuments$/);
    await page.getByLabel("Email").fill("confirmed@example.com");
    await page.getByLabel("Password").fill(PASSWORD);
    await page.getByRole("button", { name: "Sign in" }).click();
    await expect(page).toHaveURL(/\/en\/documents$/);
    await expect(page.getByTestId("account-email")).toHaveText("confirmed@example.com");
  });

  test("signed-in users visiting sign-in, register or forgot are redirected into the app", async ({
    page,
  }) => {
    await signInAndLand(page, "confirmed@example.com");
    for (const p of ["sign-in", "register", "forgot-password", "reset-password"]) {
      await page.goto(`/en/${p}`);
      await expect(page).toHaveURL(/\/en\/documents$/);
    }
  });

  for (const evil of [
    "https://evil.example/",
    "//evil.example/",
    "/\\evil.example",
    "javascript:alert(1)",
    "/en/sign-in",
    "/api/proxy/me",
  ]) {
    test(`next=${evil} never leaves the site or the app (UI-SEC-10)`, async ({ page }) => {
      await signIn(
        page,
        "confirmed@example.com",
        PASSWORD,
        `/en/sign-in?next=${encodeURIComponent(evil)}`,
      );
      await expect(page).toHaveURL(/\/en\/documents$/);
    });
  }

  test("next keeps a same-site path and the locale", async ({ page }) => {
    await signIn(page, "confirmed@example.com", PASSWORD, "/en/sign-in?next=%2Far%2Fprivacy");
    await expect(page).toHaveURL(/\/ar\/privacy$/);
  });

  test("sign-out clears the session, calls no GET, and signs out other tabs on their next request (UI-AUTH-7)", async ({
    page,
    context,
  }) => {
    await signInAndLand(page, "confirmed@example.com");
    const other = await context.newPage();
    await other.goto("/en/documents");
    await expect(other.getByTestId("account-email")).toBeVisible();

    const posts: string[] = [];
    page.on("request", (r) => r.method() !== "GET" && posts.push(r.method()));
    await page.getByRole("button", { name: "Sign out" }).click();
    await expect(page).toHaveURL(/\/en$/);
    expect(posts).toContain("POST");
    await expect(page.getByRole("link", { name: "Sign in" }).first()).toBeVisible();

    await other.reload();
    await expect(other).toHaveURL(/\/en\/sign-in\?next=/);
    expect((await context.cookies()).filter((c) => c.name.includes("docagent_session"))).toEqual(
      [],
    );
  });
});

test.describe("session renewal (UI-AUTH-8)", () => {
  test("a nearly expired access token is refreshed transparently and the user stays signed in", async ({
    page,
  }) => {
    const email = "shortlived@example.com";
    const before = (await mockState()).refreshByEmail[email] ?? 0;
    await signInAndLand(page, email); // access token lives 1 s: the proxy refreshes before rendering /documents
    expect((await mockState()).refreshByEmail[email]).toBeGreaterThan(before);
    await page.reload();
    await expect(page.getByTestId("account-email")).toHaveText(email);
  });

  test("a 401 from the API triggers one refresh and a retry in the proxy route", async ({
    page,
  }) => {
    const email = await newUser();
    await signInAndLand(page, email);
    const before = (await mockState()).refreshByEmail[email] ?? 0;
    await mock("/__test/expire", { email }); // the API now rejects the access token
    const res = await page.request.get("/api/proxy/me");
    expect(res.status()).toBe(200);
    expect((await res.json()).email).toBe(email);
    expect((await mockState()).refreshByEmail[email]).toBe(before + 1);
  });

  test("concurrent calls share one refresh", async ({ page }) => {
    const email = await newUser();
    await signInAndLand(page, email);
    const before = (await mockState()).refreshByEmail[email] ?? 0;
    await mock("/__test/expire", { email });
    const results = await Promise.all(
      Array.from({ length: 5 }, () => page.request.get("/api/proxy/me")),
    );
    expect(results.map((r) => r.status())).toEqual([200, 200, 200, 200, 200]);
    expect((await mockState()).refreshByEmail[email]).toBe(before + 1);
  });

  test("a refresh the API refuses ends the session: sign-in with the page to return to", async ({
    page,
  }) => {
    await signIn(page, "expiring@example.com");
    await expect(page).toHaveURL(/\/en\/sign-in\?next=%2Fen%2Fdocuments$/);

    const email = await newUser();
    await signInAndLand(page, email);
    await mock("/__test/revoke", { email });
    const res = await page.request.get("/api/proxy/me");
    expect(res.status()).toBe(401);
    expect((await res.json()).code).toBe("session_ended");
    await page.goto("/en/documents");
    await expect(page).toHaveURL(/\/en\/sign-in\?next=%2Fen%2Fdocuments$/);
  });
});

test.describe("Arabic", () => {
  test("auth pages are right-to-left with Arabic text and messages", async ({ page }) => {
    await page.goto("/ar/sign-in");
    await expect(page.locator("html")).toHaveAttribute("dir", "rtl");
    await expect(page.getByRole("heading", { level: 1 })).toHaveText("تسجيل الدخول");
    await page.getByLabel("البريد الإلكتروني").fill("confirmed@example.com");
    await page.getByLabel("كلمة المرور").fill("Example-Wrong-3");
    await page.getByRole("button", { name: "تسجيل الدخول" }).click();
    await expect(page.locator("main [role=alert]")).toContainText("غير صحيحة");

    await page.goto("/ar/register");
    await page.getByRole("button", { name: "إنشاء الحساب" }).click();
    await expect(page.getByText("أدخل عنوان بريدك الإلكتروني.").first()).toBeVisible();
  });

  test("the full path works in Arabic: sign in lands on the Arabic app page", async ({ page }) => {
    await page.goto("/ar/sign-in");
    await page.getByLabel("البريد الإلكتروني").fill("confirmed@example.com");
    await page.getByLabel("كلمة المرور").fill(PASSWORD);
    await page.getByRole("button", { name: "تسجيل الدخول" }).click();
    await expect(page).toHaveURL(/\/ar\/documents$/);
    await expect(page.getByTestId("account-email")).toHaveText("confirmed@example.com");
    await page.getByRole("button", { name: "تسجيل الخروج" }).click();
    await expect(page).toHaveURL(/\/ar$/);
  });
});

test.describe("accessibility of the auth pages", () => {
  const pages = [
    "sign-in",
    "register",
    "check-email",
    "forgot-password",
    "reset-password",
    "confirm?userId=u&code=c",
  ];
  for (const locale of ["en", "ar"]) {
    test(`no serious or critical axe violations (${locale}, light and dark)`, async ({ page }) => {
      for (const scheme of ["light", "dark"] as const) {
        await page.emulateMedia({ colorScheme: scheme, reducedMotion: "reduce" });
        for (const p of pages) {
          await page.goto(`/${locale}/${p}`);
          const results = await new AxeBuilder({ page })
            .withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"])
            .analyze();
          const bad = results.violations.filter(
            (v) => v.impact === "serious" || v.impact === "critical",
          );
          expect(bad, `${locale}/${p} ${scheme}: ${JSON.stringify(bad.map((v) => v.id))}`).toEqual(
            [],
          );
        }
      }
    });

    test(`no serious axe violations on the signed-in page and with errors shown (${locale})`, async ({
      page,
    }) => {
      await page.goto(`/${locale}/register`);
      await page.locator("form button[type=submit]").click(); // validation errors on screen
      let results = await new AxeBuilder({ page })
        .withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"])
        .analyze();
      expect(
        results.violations.filter((v) => v.impact === "serious" || v.impact === "critical"),
      ).toEqual([]);

      await page.goto(`/${locale}/sign-in`);
      await page
        .getByLabel(locale === "en" ? "Email" : "البريد الإلكتروني")
        .fill("unconfirmed@example.com");
      await page.getByLabel(locale === "en" ? "Password" : "كلمة المرور").fill(PASSWORD);
      await page.locator("form button[type=submit]").click();
      await expect(page).toHaveURL(new RegExp(`/${locale}/documents$`));
      results = await new AxeBuilder({ page })
        .withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"])
        .analyze();
      expect(
        results.violations.filter((v) => v.impact === "serious" || v.impact === "critical"),
      ).toEqual([]);
    });
  }

  test("the auth forms have no horizontal scroll at 320 px", async ({ page }) => {
    await page.setViewportSize({ width: 320, height: 700 });
    for (const p of ["register", "reset-password"]) {
      await page.goto(`/ar/${p}`);
      const overflow = await page.evaluate(
        () => document.documentElement.scrollWidth - document.documentElement.clientWidth,
      );
      expect(overflow).toBeLessThanOrEqual(0);
    }
  });
});
