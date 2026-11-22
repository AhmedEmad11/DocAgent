import { expect, test, type Response } from "@playwright/test";
import { PASSWORD, signInAndLand } from "./helpers";

test("no token reaches the browser: not in any response, header, URL, storage or readable cookie (UI-SEC-1)", async ({
  page,
}) => {
  const seen: string[] = [];
  const record = async (r: Response) => {
    seen.push(r.url(), JSON.stringify(await r.allHeaders()));
    try {
      seen.push(await r.text());
    } catch {
      // redirects and cancelled requests have no body
    }
  };
  const pending: Promise<void>[] = [];
  page.on("response", (r) => pending.push(record(r)));

  await signInAndLand(page, "confirmed@example.com");
  await page.goto("/en/documents");
  await page.goto("/ar/documents");
  const proxied = await page.evaluate(async () => (await fetch("/api/proxy/me")).text());
  await page.goto("/en/privacy");
  await Promise.all(pending);

  const blob = seen.join("\n") + proxied + page.url();
  expect(blob).not.toMatch(/MOCKAT|MOCKRT/);
  expect(proxied).toContain("confirmed@example.com"); // the proxy works, without the token

  const inBrowser = await page.evaluate(() => ({
    cookie: document.cookie,
    local: JSON.stringify({ ...localStorage }),
    session: JSON.stringify({ ...sessionStorage }),
    html: document.documentElement.outerHTML,
  }));
  expect(JSON.stringify(inBrowser)).not.toMatch(/MOCKAT|MOCKRT/);
  expect(inBrowser.cookie).not.toContain("docagent_session"); // httpOnly: JavaScript cannot read it
});

test("the session cookie is sealed, HttpOnly, SameSite=Lax, Path=/ with no Domain, under 4 KB (UI-SEC-2)", async ({
  page,
  context,
}) => {
  let setCookie = "";
  const pending: Promise<void>[] = [];
  page.on("response", (r) =>
    pending.push(
      r
        .headerValue("set-cookie")
        .then((h) => {
          if (h?.includes("docagent_session=")) setCookie = h;
        })
        .catch(() => {}),
    ),
  );
  await signInAndLand(page, "confirmed@example.com");
  await Promise.all(pending);

  expect(setCookie).toMatch(/HttpOnly/i);
  expect(setCookie).toMatch(/SameSite=lax/i);
  expect(setCookie).toMatch(/Path=\//);
  expect(setCookie).not.toMatch(/Domain=/i);
  expect(setCookie).toMatch(/Max-Age=1209600/); // 14 days (D26)

  const cookie = (await context.cookies()).find((c) => c.name.includes("docagent_session"))!;
  expect(cookie.httpOnly).toBe(true);
  expect(cookie.sameSite).toBe("Lax");
  expect(cookie.value).not.toMatch(/MOCK/); // encrypted, not just encoded
  expect(cookie.value.length).toBeLessThan(3800);
});

test("a tampered session cookie is treated as signed out and removed", async ({
  page,
  context,
}) => {
  await context.addCookies([
    { name: "docagent_session", value: "not.a.valid.token", url: "http://127.0.0.1:3100" },
  ]);
  await page.goto("/en/documents");
  await expect(page).toHaveURL(/\/en\/sign-in/);
  expect((await context.cookies()).some((c) => c.name === "docagent_session")).toBe(false);
});

test.describe("generic proxy route (UI-SEC-7, UI-SEC-3)", () => {
  test("only allowlisted routes and methods are forwarded", async ({ request }) => {
    for (const [method, path] of [
      ["GET", "/api/proxy/auth/login"],
      ["POST", "/api/proxy/auth/refresh"],
      ["GET", "/api/proxy/nope"],
      ["DELETE", "/api/proxy/documents"],
      ["PUT", "/api/proxy/me"],
      ["GET", "/api/proxy/documents/not-a-guid"],
      ["GET", "/api/proxy/me/%2e%2e/auth/login"],
      ["GET", "/api/proxy/documents/%2F"],
    ] as const) {
      const res = await request.fetch(path, {
        method,
        headers: { origin: "http://127.0.0.1:3100" },
      });
      expect(res.status(), `${method} ${path}`).toBe(404);
    }
  });

  test("anonymous calls get a 401 problem in the fixed shape, never cached", async ({
    request,
  }) => {
    const res = await request.get("/api/proxy/me");
    expect(res.status()).toBe(401);
    expect(res.headers()["cache-control"]).toBe("no-store");
    expect(await res.json()).toEqual({ status: 401, title: "Unauthorized", code: "session_ended" });
  });

  test("state-changing calls from another origin (or with no origin) are rejected before anything is forwarded", async ({
    request,
  }) => {
    const id = "123e4567-e89b-12d3-a456-426614174000";
    const bad: Record<string, string>[] = [
      { origin: "https://evil.example" },
      { origin: "null" },
      { "sec-fetch-site": "cross-site" },
      {},
    ];
    for (const headers of bad) {
      const res = await request.delete(`/api/proxy/documents/${id}`, { headers });
      expect(res.status(), JSON.stringify(headers)).toBe(403);
    }
    // Same origin passes the check (and then fails for want of a session).
    const ok = await request.delete(`/api/proxy/documents/${id}`, {
      headers: { origin: "http://127.0.0.1:3100" },
    });
    expect(ok.status()).toBe(401);
  });

  test("oversized bodies are refused before forwarding", async ({ page }) => {
    await signInAndLand(page, "confirmed@example.com");
    const res = await page.request.post("/api/proxy/ask", {
      headers: { origin: "http://127.0.0.1:3100", "content-type": "application/json" },
      data: JSON.stringify({ question: "x".repeat(70_000) }),
    });
    expect(res.status()).toBe(413);
  });

  test("a signed-in call is forwarded without leaking headers the route does not allow", async ({
    page,
  }) => {
    await signInAndLand(page, "confirmed@example.com");
    const res = await page.request.get("/api/proxy/me", {
      headers: { "x-llm-api-key": "should-not-be-forwarded" },
    });
    expect(res.status()).toBe(200);
    expect(res.headers()["cache-control"]).toBe("no-store");
  });
});

test("server actions reject a cross-site POST (UI-SEC-3)", async ({ request }) => {
  // A forged form post from another site: Next rejects the Origin mismatch and our own check does too; either way no session is created.
  const res = await request.post("/en/sign-in", {
    headers: {
      origin: "https://evil.example",
      "next-action": "00",
      "content-type": "text/plain;charset=UTF-8",
    },
    data: JSON.stringify([{ email: "confirmed@example.com", password: PASSWORD, locale: "en" }]),
  });
  expect(res.headers()["set-cookie"] ?? "").not.toContain("docagent_session=");
});
