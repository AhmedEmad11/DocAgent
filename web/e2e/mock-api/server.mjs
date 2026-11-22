// Mock of the DocAgent API for e2e (plan 9.0, SRS 10.5). It mirrors the real responses of ASP.NET Core Identity's API endpoints
// (verified against the plan's A8 notes): register/reset validation problems keyed by Identity error code, login 401 with
// detail "Failed"/"LockedOut", /refresh returning a NEW refresh token while the old one stays valid, confirmEmail 200 text or 401.
// Endpoints for documents and ask are added in 9.2 and 9.3. Everything in memory; test hooks live under /__test/.
import { createServer } from "node:http";
import { randomBytes } from "node:crypto";

const port = Number(process.env.MOCK_API_PORT ?? 4010);
const BFF_SECRET = process.env.MOCK_BFF_SECRET ?? "example-only-bff-secret";
const ALLOWED_RETURN =
  process.env.MOCK_ALLOWED_RETURN ?? "http://127.0.0.1:3100/api/auth/external/callback";
const PASSWORD = "Example-Only-1";
const RATE_LIMITED_IP = "198.51.100.99"; // a visitor the mock throttles, to test Retry-After and X-Client-IP forwarding

const rand = (n = 6) => randomBytes(n).toString("hex");
const users = new Map(); // email -> {id, password, confirmed, externalLogins, failures}
const accessTokens = new Map(); // token -> {email, expired}
const refreshTokens = new Map(); // token -> {email, revoked}
const mailbox = new Map(); // email -> {confirm: {userId, code}, resetCode}
const externalCodes = new Map(); // code -> email (single use)
const state = {
  refreshCalls: 0,
  refreshByEmail: {},
  loginCalls: 0,
  lastClientIp: null,
  loginIpByEmail: {},
  untrustedCalls: 0,
  externalOutcome: "ok",
  lastExchangeCode: null,
};

function seed() {
  const add = (email, extra = {}) =>
    users.set(email, {
      id: crypto.randomUUID(),
      password: PASSWORD,
      confirmed: true,
      externalLogins: [],
      failures: 0,
      ...extra,
    });
  add("confirmed@example.com");
  add("unconfirmed@example.com", { confirmed: false });
  add("locked@example.com", { locked: true });
  add("shortlived@example.com", { shortLived: true });
  add("expiring@example.com", { shortLived: true, refreshRevoked: true }); // access token about to expire, refresh refused
  add("social@example.com", { externalLogins: ["Google"], password: null });
}
seed();

function send(res, status, body, headers = {}) {
  const isText = typeof body === "string";
  res.writeHead(status, {
    "content-type": isText ? "text/plain; charset=utf-8" : "application/problem+json",
    ...headers,
  });
  res.end(isText ? body : body === undefined ? undefined : JSON.stringify(body));
}
const json = (res, status, body) => {
  res.writeHead(status, { "content-type": "application/json" });
  res.end(JSON.stringify(body));
};
const problem = (res, status, title, extra = {}, headers = {}) =>
  send(res, status, { type: "about:blank", title, status, ...extra }, headers);

async function readJson(req) {
  const chunks = [];
  for await (const c of req) chunks.push(c);
  try {
    return JSON.parse(Buffer.concat(chunks).toString("utf8") || "{}");
  } catch {
    return {};
  }
}

const IDENTITY_MESSAGES = {
  PasswordTooShort: "Passwords must be at least 6 characters.",
  PasswordRequiresNonAlphanumeric: "Passwords must have at least one non alphanumeric character.",
  PasswordRequiresDigit: "Passwords must have at least one digit ('0'-'9').",
  PasswordRequiresLower: "Passwords must have at least one lowercase ('a'-'z').",
  PasswordRequiresUpper: "Passwords must have at least one uppercase ('A'-'Z').",
};
function passwordErrors(password) {
  const e = {};
  const add = (code) => (e[code] = [IDENTITY_MESSAGES[code]]);
  if (password.length < 6) add("PasswordTooShort");
  if (!/[^A-Za-z0-9]/.test(password)) add("PasswordRequiresNonAlphanumeric");
  if (!/[0-9]/.test(password)) add("PasswordRequiresDigit");
  if (!/[a-z]/.test(password)) add("PasswordRequiresLower");
  if (!/[A-Z]/.test(password)) add("PasswordRequiresUpper");
  return e;
}
const validation = (res, errors) =>
  send(res, 400, {
    type: "https://tools.ietf.org/html/rfc9110#section-15.5.1",
    title: "One or more validation errors occurred.",
    status: 400,
    errors,
  });

function issueTokens(email) {
  const user = users.get(email);
  const accessToken = `MOCKAT.${rand(12)}`;
  const refreshToken = `MOCKRT.${rand(12)}`;
  accessTokens.set(accessToken, { email, expired: false });
  refreshTokens.set(refreshToken, { email, revoked: Boolean(user?.refreshRevoked) });
  return { tokenType: "Bearer", accessToken, expiresIn: user?.shortLived ? 1 : 3600, refreshToken };
}

function newConfirmation(email) {
  const user = users.get(email);
  const box = mailbox.get(email) ?? {};
  box.confirm = { userId: user.id, code: `CONF-${rand()}` };
  mailbox.set(email, box);
}

const norm = (e) =>
  String(e ?? "")
    .trim()
    .toLowerCase();

async function handle(req, res) {
  const url = new URL(req.url ?? "/", "http://mock");
  const path = url.pathname;

  if (path === "/health") return json(res, 200, { status: "ok" });

  // ---- test hooks ----
  if (path.startsWith("/__test/")) {
    if (path === "/__test/state") return json(res, 200, state);
    if (path === "/__test/reset") {
      Object.assign(state, {
        refreshCalls: 0,
        refreshByEmail: {},
        loginCalls: 0,
        lastClientIp: null,
        untrustedCalls: 0,
        externalOutcome: "ok",
        lastExchangeCode: null,
      });
      return json(res, 200, state);
    }
    if (path === "/__test/mailbox")
      return json(res, 200, mailbox.get(norm(url.searchParams.get("email"))) ?? {});
    const body = await readJson(req);
    if (path === "/__test/external") {
      state.externalOutcome = String(body.outcome ?? "ok");
      return json(res, 200, state);
    }
    if (path === "/__test/user") {
      // Isolated account for tests that expire or revoke tokens (they would affect parallel tests on a shared user).
      const email = norm(body.email);
      users.set(email, {
        id: crypto.randomUUID(),
        password: PASSWORD,
        confirmed: body.confirmed !== false,
        externalLogins: [],
        failures: 0,
      });
      return json(res, 200, { email });
    }
    if (path === "/__test/expire") {
      for (const t of accessTokens.values()) if (t.email === norm(body.email)) t.expired = true;
      return json(res, 200, {});
    }
    if (path === "/__test/revoke") {
      for (const t of refreshTokens.values()) if (t.email === norm(body.email)) t.revoked = true;
      for (const t of accessTokens.values()) if (t.email === norm(body.email)) t.expired = true;
      return json(res, 200, {});
    }
    return problem(res, 404, "Not Found");
  }

  const prefix = "/api/v1";
  if (!path.startsWith(prefix)) return problem(res, 404, "Not Found");
  const route = path.slice(prefix.length);

  // The API trusts X-Client-IP only with the matching shared secret (plan A4).
  const clientIp = req.headers["x-client-ip"];
  const trusted = req.headers["x-bff-secret"] === BFF_SECRET && typeof clientIp === "string";
  if (clientIp && !trusted) state.untrustedCalls++;
  if (trusted) state.lastClientIp = clientIp;
  const throttled = trusted && clientIp === RATE_LIMITED_IP && route.startsWith("/auth/");

  if (route.startsWith("/auth/") && throttled && route !== "/auth/external/callback") {
    if (route === "/auth/login") state.loginIpByEmail[norm((await readJson(req)).email)] = clientIp;
    return problem(
      res,
      429,
      "Too many requests",
      { detail: "Too many attempts. Try again later." },
      { "retry-after": "7" },
    );
  }

  if (route === "/auth/register" && req.method === "POST") {
    const { email, password } = await readJson(req);
    const e = norm(email);
    const errors = {};
    if (!/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(e))
      errors.InvalidEmail = [`Email '${email}' is invalid.`];
    Object.assign(errors, passwordErrors(String(password ?? "")));
    if (users.has(e)) {
      errors.DuplicateUserName = [`Username '${e}' is already taken.`];
      errors.DuplicateEmail = [`Email '${e}' is already taken.`];
    }
    if (Object.keys(errors).length > 0) return validation(res, errors);
    users.set(e, {
      id: crypto.randomUUID(),
      password,
      confirmed: false,
      externalLogins: [],
      failures: 0,
    });
    newConfirmation(e);
    res.writeHead(200);
    return res.end();
  }

  if (route === "/auth/login" && req.method === "POST") {
    state.loginCalls++;
    const { email, password } = await readJson(req);
    state.loginIpByEmail[norm(email)] = trusted ? clientIp : null; // what the API would use for the rate-limit bucket
    const user = users.get(norm(email));
    if (user?.locked) return problem(res, 401, "Unauthorized", { detail: "LockedOut" });
    if (!user || user.password === null || user.password !== password)
      return problem(res, 401, "Unauthorized", { detail: "Failed" });
    return json(res, 200, issueTokens(norm(email)));
  }

  if (route === "/auth/refresh" && req.method === "POST") {
    state.refreshCalls++;
    const { refreshToken } = await readJson(req);
    const rec = refreshTokens.get(refreshToken);
    if (!rec || rec.revoked) {
      res.writeHead(401);
      return res.end();
    }
    state.refreshByEmail[rec.email] = (state.refreshByEmail[rec.email] ?? 0) + 1;
    // A new pair every time; the old refresh token stays valid (stateless tickets, plan A8).
    const user = users.get(rec.email);
    const wasShort = user?.shortLived;
    if (user) user.shortLived = false;
    const tokens = issueTokens(rec.email);
    if (user) user.shortLived = wasShort;
    return json(res, 200, tokens);
  }

  if (route === "/auth/resendConfirmationEmail" && req.method === "POST") {
    const { email } = await readJson(req);
    const e = norm(email);
    if (users.get(e) && !users.get(e).confirmed) newConfirmation(e);
    res.writeHead(200);
    return res.end();
  }

  if (route === "/auth/forgotPassword" && req.method === "POST") {
    const { email } = await readJson(req);
    const e = norm(email);
    if (users.get(e)?.confirmed)
      mailbox.set(e, { ...(mailbox.get(e) ?? {}), resetCode: `RESET-${rand()}` });
    res.writeHead(200);
    return res.end();
  }

  if (route === "/auth/resetPassword" && req.method === "POST") {
    const { email, resetCode, newPassword } = await readJson(req);
    const e = norm(email);
    const box = mailbox.get(e);
    if (!users.has(e) || !box?.resetCode || box.resetCode !== resetCode)
      return validation(res, { InvalidToken: ["Invalid token."] });
    const errors = passwordErrors(String(newPassword ?? ""));
    if (Object.keys(errors).length > 0) return validation(res, errors);
    users.get(e).password = newPassword;
    delete box.resetCode;
    res.writeHead(200);
    return res.end();
  }

  if (route === "/auth/confirmEmail" && req.method === "GET") {
    const userId = url.searchParams.get("userId");
    const code = url.searchParams.get("code");
    for (const [email, box] of mailbox) {
      if (box.confirm && box.confirm.userId === userId && box.confirm.code === code) {
        users.get(email).confirmed = true;
        return send(res, 200, "Thank you for confirming your email.");
      }
    }
    res.writeHead(401);
    return res.end();
  }

  const externalStart = route.match(/^\/auth\/external\/(google|github)$/);
  if (externalStart && req.method === "GET") {
    const returnUrl = url.searchParams.get("returnUrl") ?? "";
    if (!returnUrl.startsWith(ALLOWED_RETURN))
      return problem(res, 400, "Bad request", { detail: "returnUrl is missing or not allowed." });
    let outcome = state.externalOutcome;
    let query;
    if (outcome === "ok") {
      const code = `EXT-${rand()}`;
      externalCodes.set(code, "social@example.com");
      query = `code=${code}`;
    } else if (outcome === "bad_code") {
      query = "code=EXT-unknown";
    } else {
      query = `error=${outcome}`;
    }
    res.writeHead(302, { location: `${returnUrl}${returnUrl.includes("?") ? "&" : "?"}${query}` });
    return res.end();
  }

  if (route === "/auth/external/exchange" && req.method === "POST") {
    const { code } = await readJson(req);
    state.lastExchangeCode = code ?? null;
    const email = externalCodes.get(code);
    if (!email)
      return problem(res, 400, "Bad request", { detail: "The code is invalid or has expired." });
    externalCodes.delete(code); // single use
    return json(res, 200, issueTokens(email));
  }

  if (route === "/me" && req.method === "GET") {
    const token = /^Bearer (.+)$/.exec(req.headers.authorization ?? "")?.[1];
    const rec = token && accessTokens.get(token);
    if (!rec || rec.expired) {
      res.writeHead(401);
      return res.end();
    }
    const user = users.get(rec.email);
    return json(res, 200, {
      id: user.id,
      email: rec.email,
      emailConfirmed: user.confirmed,
      externalLogins: user.externalLogins,
      hasLlmKey: false,
      demo: null,
      chunks: { used: 0, limit: 0 },
    });
  }

  return problem(res, 404, "Not Found");
}

const server = createServer((req, res) => {
  handle(req, res).catch(() => {
    if (!res.headersSent) problem(res, 500, "Server error");
  });
});

server.listen(port, "127.0.0.1", () => {
  console.log(`mock API listening on http://127.0.0.1:${port}`);
});
