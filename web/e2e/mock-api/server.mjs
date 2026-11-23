// Mock of the DocAgent API for e2e (plan 9.0, SRS 10.5). It mirrors the real responses of ASP.NET Core Identity's API endpoints
// (verified against the plan's A8 notes): register/reset validation problems keyed by Identity error code, login 401 with
// detail "Failed"/"LockedOut", /refresh returning a NEW refresh token while the old one stays valid, confirmEmail 200 text or 401.
// Documents (9.2) include the upload-ticket flow with CORS on the upload route only (plan A3); ask and the SSE stream (9.3) are scripted by
// keywords in the question (see askScript).
// Everything in memory; test hooks live under /__test/.
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
  ticketsIssued: 0,
  uploads: [], // {name, size, auth: "ticket" | "bearer"}
  listCalls: 0,
  ask: { started: 0, aborted: 0, abortedQuestions: [], completed: 0, bodies: [] },
  workflows: [],
};
const tickets = new Map(); // ticket -> {email, expires}
const UI_ORIGIN = process.env.MOCK_UI_ORIGIN ?? "http://127.0.0.1:3100";
const LIMITS = {
  demo: { documents: 3, bytes: 5 * 1024 * 1024, chunks: 3000 },
  byok: { documents: 100, bytes: 25 * 1024 * 1024, chunks: 20000 },
};
const SCANNED = "No extractable text: scanned PDFs are not supported.";
const STORAGE =
  "Storage limit reached: 2,990 of 3,000 chunks used, and this document needs 40 more. Delete a document to free space.";
const CONTENT_TYPES = {
  ".pdf": "application/pdf",
  ".docx": "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
  ".txt": "text/plain",
  ".md": "text/markdown",
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

async function readBody(req) {
  const chunks = [];
  for await (const c of req) chunks.push(c);
  return Buffer.concat(chunks);
}

/** Finds the `file` part of a multipart body: name and size of its content. */
function parseMultipartFile(buf, contentType) {
  const boundary = /boundary=(?:"([^"]+)"|([^;]+))/i.exec(contentType);
  if (!boundary) return null;
  const marker = Buffer.from(`--${boundary[1] ?? boundary[2]}`);
  const start = buf.indexOf(marker);
  if (start < 0) return null;
  const headEnd = buf.indexOf("\r\n\r\n", start);
  if (headEnd < 0) return null;
  const head = buf.subarray(start, headEnd).toString("utf8");
  if (!/name="file"/.test(head)) return null;
  const name = /filename="([^"]*)"/.exec(head)?.[1];
  const end = buf.indexOf(Buffer.concat([Buffer.from("\r\n"), marker]), headEnd + 4);
  const size = (end < 0 ? buf.length : end) - (headEnd + 4);
  return name && size > 0 ? { name, size } : null;
}

function makeDocument(fileName, sizeBytes, opts) {
  const ext = (fileName.match(/\.[^.]+$/)?.[0] ?? "").toLowerCase();
  const scanned = fileName.includes("scan");
  const storage = fileName.includes("storage");
  return {
    id: opts.id ?? crypto.randomUUID(),
    fileName,
    contentType: opts.contentType ?? CONTENT_TYPES[ext] ?? "text/plain",
    sizeBytes,
    pageCount: opts.pageCount ?? (ext === ".pdf" ? 3 : null),
    chunkCount: opts.chunkCount ?? Math.max(1, Math.ceil(sizeBytes / 1000)),
    status: opts.status ?? "Pending",
    hold: opts.status !== undefined, // seeded with an explicit status: stays as seeded
    finalStatus: scanned || storage ? "Failed" : "Ready",
    failureReason: opts.failureReason ?? (scanned ? SCANNED : storage ? STORAGE : null),
    extractionNotice:
      opts.extractionNotice ??
      (fileName.includes("arabic")
        ? "Arabic text was repaired after extraction; check the wording."
        : null),
    createdAt: opts.createdAt ?? new Date().toISOString(),
  };
}

/** One step per list call: Pending -> Processing -> Ready/Failed. */
function advance(d) {
  if (d.hold) return;
  if (d.status === "Pending") d.status = "Processing";
  else if (d.status === "Processing") d.status = d.finalStatus;
}

function publicDoc(d) {
  const { hold, finalStatus, ...rest } = d;
  void hold;
  void finalStatus;
  const out = { ...rest };
  if (out.status !== "Failed") out.failureReason = null;
  if (out.status === "Pending" || out.status === "Processing") out.chunkCount = 0;
  return out;
}

const PRIVACY =
  "Demo mode: your questions and the matching document excerpts are sent to a free third-party model that may log them.";

/** The answer for a question, scripted by keywords (see the ask handler). */
function makeAnswer(q, ready, documentIds) {
  const scope =
    Array.isArray(documentIds) && documentIds.length
      ? ready.filter((d) => documentIds.includes(d.id))
      : ready;
  const doc = scope[0];
  const cite = (index, quote, extra = {}) => ({
    index,
    documentId: doc.id,
    fileName: doc.fileName,
    pageNumber: 3,
    section: null,
    quote,
    ...extra,
  });
  const base = {
    answer:
      "Rent is due on the first day of each month [1]. A late fee of 5% applies after ten days [2].",
    notFound: false,
    citations: [
      cite(1, "Rent is payable on the first day of each month."),
      cite(2, "A late fee of 5% applies after ten days.", { pageNumber: 4 }),
    ],
    mode: "byok",
    provider: "openrouter",
    model: "example/test-model",
    incomplete: false,
    toolCalls: 2,
    notices: [],
  };
  if (q.includes("notfound"))
    return { ...base, answer: "The documents do not mention this.", notFound: true, citations: [] };
  if (q.includes("incomplete")) return { ...base, incomplete: true };
  if (q.includes("demomode"))
    return {
      ...base,
      mode: "demo",
      provider: "openrouter",
      model: "free/demo-model",
      notices: [PRIVACY],
    };
  if (q.includes("notice"))
    return { ...base, notices: ["Arabic text was repaired after extraction; check the wording."] };
  if (q.includes("arabic") || /[؀-ۿ]/.test(q))
    return {
      ...base,
      answer:
        "يستحق الإيجار في اليوم الأول من كل شهر [1]. تُطبَّق غرامة تأخير بعد عشرة أيام [2].\n\nRent is due on the 1st.",
      citations: [
        cite(1, "يُدفع الإيجار في اليوم الأول من كل شهر.", { section: "البند 4" }),
        cite(2, "A late fee applies after ten days."),
      ],
    };
  if (q.includes("inject"))
    return {
      ...base,
      answer:
        'Safe text [1] <script>window.__pwned=1</script> <img src=x onerror="window.__pwned=1">\n\n[click me](javascript:window.__pwned=1) and [fine](https://example.com/page) and ![pixel](https://evil.test/p.png)\n\n<iframe src="https://evil.test"></iframe>',
      citations: [cite(1, "A quote with <b>markup</b> and a [link](javascript:alert(1)).")],
    };
  if (q.includes("badcitation"))
    return {
      ...base,
      answer: "Only one source [1], and a marker without a source [9].",
      citations: [cite(1, "Only quote.")],
    };
  if (q.includes("long"))
    return {
      ...base,
      answer: Array.from({ length: 12 }, (_, i) => `Paragraph ${i + 1} with some words [1].`).join(
        "\n\n",
      ),
    };
  return base;
}

/** Demo accounts (no own key, demo switched on by a test hook) have a daily allowance that ask, summary and compare use up. */
const demoActive = (user) => Boolean(user.demo) && !user.hasKey;
const demoBody = (user) =>
  demoActive(user)
    ? {
        limit: user.demo.limit,
        used: user.demo.used,
        remaining: Math.max(0, user.demo.limit - user.demo.used),
        resetsAt: user.demo.resetsAt,
        privacyNotice: PRIVACY,
      }
    : null;
const quotaProblem = (res, problemFn) =>
  problemFn(
    res,
    429,
    "Daily demo limit reached",
    { code: "demo_quota_exceeded", detail: "You have used today's demo questions." },
    { "retry-after": "3600" },
  );

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
        ticketsIssued: 0,
        uploads: [],
        listCalls: 0,
        ask: { started: 0, aborted: 0, abortedQuestions: [], completed: 0, bodies: [] },
        workflows: [],
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
    if (path === "/__test/documents") {
      // Seeds documents (status held, so the list does not advance) and optionally switches the tier.
      const user = users.get(norm(body.email));
      if (body.hasKey !== undefined) user.hasKey = Boolean(body.hasKey);
      if (body.chunksUsed !== undefined) user.chunksUsed = body.chunksUsed;
      user.docs = (body.documents ?? []).map((d) =>
        makeDocument(d.fileName, d.sizeBytes ?? 1000, d),
      );
      return json(res, 200, { count: user.docs.length });
    }
    if (path === "/__test/demo") {
      const user = users.get(norm(body.email));
      user.demo =
        body.enabled === false
          ? null
          : {
              limit: body.limit ?? 10,
              used: body.used ?? 0,
              resetsAt: body.resetsAt ?? new Date(Date.now() + 6 * 3600_000).toISOString(),
            };
      if (body.hasKey !== undefined) user.hasKey = Boolean(body.hasKey);
      return json(res, 200, { demo: demoBody(user) });
    }
    if (path === "/__test/next-upload-error") {
      state.nextUploadError = body.status
        ? { status: body.status, code: body.code, retryAfter: body.retryAfter }
        : null;
      return json(res, 200, {});
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

  // ---- documents (9.2) ----
  const bearer = /^Bearer (.+)$/.exec(req.headers.authorization ?? "")?.[1];
  const bearerRec = bearer && accessTokens.get(bearer);
  const bearerUser = bearerRec && !bearerRec.expired ? bearerRec.email : null;

  // CORS for the one route the browser calls directly (A3): only the UI origin, no credentials.
  const origin = req.headers.origin;
  const corsHeaders =
    origin === UI_ORIGIN
      ? {
          "access-control-allow-origin": origin,
          "access-control-expose-headers": "Retry-After",
          vary: "Origin",
        }
      : {};
  if (route === "/documents" && req.method === "OPTIONS") {
    if (origin !== UI_ORIGIN) {
      res.writeHead(403);
      return res.end();
    }
    res.writeHead(204, {
      ...corsHeaders,
      "access-control-allow-methods": "POST",
      "access-control-allow-headers": "X-Upload-Ticket, Content-Type",
      "access-control-max-age": "600",
    });
    return res.end();
  }

  if (route === "/documents/upload-ticket" && req.method === "POST") {
    if (!bearerUser) {
      res.writeHead(401);
      return res.end();
    }
    if (!users.get(bearerUser).confirmed) return problem(res, 403, "Forbidden");
    const ticket = `MOCKTICKET-${rand(16)}`;
    tickets.set(ticket, { email: bearerUser, expires: Date.now() + 60_000 });
    state.ticketsIssued++;
    return json(res, 200, { ticket, expiresInSeconds: 60 });
  }

  if (route === "/documents" && req.method === "POST") {
    // Bearer token or one-time ticket, never a cookie.
    let email = bearerUser;
    let auth = "bearer";
    const ticketValue = req.headers["x-upload-ticket"];
    if (!email && typeof ticketValue === "string") {
      const rec = tickets.get(ticketValue);
      tickets.delete(ticketValue); // single use, also when expired
      if (rec && rec.expires > Date.now()) {
        email = rec.email;
        auth = "ticket";
      }
    }
    if (!email) {
      res.writeHead(401, corsHeaders);
      return res.end();
    }
    const user = users.get(email);
    if (!user.confirmed) return problem(res, 403, "Forbidden", {}, corsHeaders);
    const tier = user.hasKey ? LIMITS.byok : LIMITS.demo;
    const raw = await readBody(req);
    const file = parseMultipartFile(raw, String(req.headers["content-type"] ?? ""));
    if (!file)
      return problem(
        res,
        400,
        "Bad request",
        { detail: "Attach a non-empty file in a form field named 'file'." },
        corsHeaders,
      );
    state.uploads.push({ name: file.name, size: file.size, auth });
    if (file.name.includes("slow")) await new Promise((r) => setTimeout(r, 1500));
    if (state.nextUploadError) {
      const e = state.nextUploadError;
      state.nextUploadError = null;
      return problem(res, e.status, "Error", e.code ? { code: e.code } : {}, {
        ...corsHeaders,
        ...(e.retryAfter ? { "retry-after": String(e.retryAfter) } : {}),
      });
    }
    if (file.name.includes("throttled"))
      return problem(
        res,
        429,
        "Too many requests",
        { detail: "Too many attempts. Try again later." },
        { ...corsHeaders, "retry-after": "2" },
      );
    const ext = (file.name.match(/\.[^.]+$/)?.[0] ?? "").toLowerCase();
    if (!CONTENT_TYPES[ext])
      return problem(
        res,
        415,
        "Unsupported file type",
        { detail: "Unsupported file type." },
        corsHeaders,
      );
    if (file.size > tier.bytes)
      return problem(
        res,
        413,
        "File too large",
        { detail: "The file is larger than the limit for your account." },
        corsHeaders,
      );
    if ((user.docs ?? []).length >= tier.documents)
      return problem(
        res,
        409,
        "Conflict",
        { detail: `You have reached the limit of ${tier.documents} documents for your account.` },
        corsHeaders,
      );
    const doc = makeDocument(file.name, file.size, {});
    user.docs = [doc, ...(user.docs ?? [])];
    res.writeHead(202, {
      "content-type": "application/json",
      ...corsHeaders,
      location: `/api/v1/documents/${doc.id}`,
    });
    return res.end(JSON.stringify({ id: doc.id, fileName: doc.fileName, status: "Pending" }));
  }

  if (route.startsWith("/documents")) {
    if (!bearerUser) {
      res.writeHead(401);
      return res.end();
    }
    const user = users.get(bearerUser);
    if (!user.confirmed) return problem(res, 403, "Forbidden");
    user.docs ??= [];
    if (route === "/documents" && req.method === "GET") {
      state.listCalls++;
      for (const d of user.docs) advance(d);
      return json(res, 200, user.docs.map(publicDoc));
    }
    const one = route.match(/^\/documents\/([0-9a-f-]{36})$/);
    if (one) {
      const d = user.docs.find((x) => x.id === one[1]);
      if (!d) return problem(res, 404, "Not found", { detail: "Document not found." });
      if (req.method === "GET") return json(res, 200, publicDoc(d));
      if (req.method === "DELETE") {
        user.docs = user.docs.filter((x) => x !== d);
        res.writeHead(204);
        return res.end();
      }
    }
  }

  // ---- summary and compare (9.4): not streamed, scripted by keywords in the focus ----
  const summaryMatch = route.match(/^\/documents\/([0-9a-f-]{36})\/summary$/);
  if ((summaryMatch || route === "/documents/compare") && req.method === "POST") {
    if (!bearerUser) {
      res.writeHead(401);
      return res.end();
    }
    const user = users.get(bearerUser);
    if (!user.confirmed) return problem(res, 403, "Forbidden");
    const body = await readJson(req);
    const focus = String(body.focus ?? "").trim();
    if (focus.length > 500)
      return problem(res, 400, "Bad request", {
        detail: "'focus' must be at most 500 characters.",
      });
    const ids = summaryMatch ? [summaryMatch[1]] : [body.documentIdA, body.documentIdB];
    if (!summaryMatch) {
      if (!body.documentIdA || !body.documentIdB)
        return problem(res, 400, "Bad request", {
          detail: "'documentIdA' and 'documentIdB' are required.",
        });
      if (body.documentIdA === body.documentIdB)
        return problem(res, 400, "Bad request", {
          detail: "Choose two different documents to compare.",
        });
    }
    const docs = ids.map((id) => (user.docs ?? []).find((d) => d.id === id));
    if (docs.some((d) => !d))
      return problem(res, 404, "Not found", { detail: "Document not found." });
    if (docs.some((d) => d.status !== "Ready"))
      return problem(res, 409, "Conflict", { detail: "The document is not ready yet." });
    if (demoActive(user) && user.demo.used >= user.demo.limit) return quotaProblem(res, problem);
    state.workflows.push({
      kind: summaryMatch ? "summary" : "compare",
      ids,
      focus,
      auth: req.headers.authorization ? "bearer" : "none",
    });
    const f = focus.toLowerCase();
    let closed = false;
    res.on("close", () => {
      if (!res.writableEnded) {
        closed = true;
        state.ask.abortedQuestions.push(`workflow:${focus}`);
      }
    });
    for (const [word, status, code] of [
      ["quota", 429, "demo_quota_exceeded"],
      ["nokey", 403, "llm_key_required"],
      ["nocredit", 402, "llm_no_credit"],
      ["unavailable", 503, "llm_unavailable"],
    ]) {
      if (f.includes(word))
        return problem(res, status, "Error", { code, detail: "English detail text." });
    }
    if (f.includes("timeout")) {
      await new Promise((r) => setTimeout(r, 300));
      return problem(res, 504, "Timed out", { detail: "The request took too long." });
    }
    if (f.includes("hang")) {
      while (!closed) await new Promise((r) => setTimeout(r, 50));
      return;
    }
    await new Promise((r) => setTimeout(r, f.includes("slow") ? 1500 : 100));
    const cite = (d, index, quote) => ({
      index,
      documentId: d.id,
      fileName: d.fileName,
      pageNumber: index + 1,
      section: null,
      quote,
    });
    const demo = f.includes("demomode") || demoActive(user);
    if (demoActive(user)) user.demo.used++;
    const result = {
      answer: summaryMatch
        ? `${docs[0].fileName} sets out the rent and the notice period [1]. Termination needs 30 days' notice [2].`
        : `Both documents cover rent [1][2]. They differ on notice: the first needs 30 days [1], the second 60 days [2].`,
      notFound: false,
      citations: summaryMatch
        ? [
            cite(docs[0], 1, "Rent is payable monthly."),
            cite(docs[0], 2, "Termination needs 30 days' notice."),
          ]
        : [
            cite(docs[0], 1, "Rent is payable monthly. Notice: 30 days."),
            cite(docs[1], 2, "Rent is payable quarterly. Notice: 60 days."),
          ],
      mode: demo ? "demo" : "byok",
      provider: "openrouter",
      model: demo ? "free/demo-model" : "example/test-model",
      incomplete: f.includes("incomplete"),
      toolCalls: 0,
      notices: [
        ...(demo ? [PRIVACY] : []),
        ...docs
          .filter((d) => d.extractionNotice)
          .map((d) => `${d.fileName}: ${d.extractionNotice}`),
      ],
    };
    return json(res, 200, result);
  }

  // ---- ask (9.3): POST /ask and POST /ask/stream, scripted by keywords in the question ----
  if ((route === "/ask" || route === "/ask/stream") && req.method === "POST") {
    if (!bearerUser) {
      res.writeHead(401);
      return res.end();
    }
    const user = users.get(bearerUser);
    if (!user.confirmed) return problem(res, 403, "Forbidden");
    const body = await readJson(req);
    const question = String(body.question ?? "").trim();
    if (!question) return problem(res, 400, "Bad request", { detail: "'question' is required." });
    if (question.length > 2000)
      return problem(res, 400, "Bad request", {
        detail: "'question' must be at most 2000 characters.",
      });
    const ready = (user.docs ?? []).filter((d) => d.status === "Ready");
    if (ready.length === 0)
      return problem(res, 409, "Conflict", {
        detail:
          "You have no documents that are ready yet. Upload a document and wait until its status is Ready.",
      });
    if (
      Array.isArray(body.documentIds) &&
      body.documentIds.some((id) => !ready.some((d) => d.id === id))
    )
      return problem(res, 400, "Bad request", {
        detail: "These documents do not exist or are not ready.",
      });
    if (demoActive(user) && user.demo.used >= user.demo.limit) return quotaProblem(res, problem);
    const q = question.toLowerCase();
    state.ask.bodies.push({
      question,
      documentIds: body.documentIds ?? null,
      auth: req.headers.authorization ? "bearer" : "none",
    });
    for (const [word, status, code] of [
      ["quota", 429, "demo_quota_exceeded"],
      ["nokey", 403, "llm_key_required"],
      ["badkey", 422, "llm_key_invalid"],
      ["nocredit", 402, "llm_no_credit"],
      ["unreadable", 409, "llm_key_unreadable"],
      ["rejected", 502, "llm_request_rejected"],
      ["unknownmodel", 422, "llm_model_unknown"],
      ["demooff", 503, "demo_unavailable"],
    ]) {
      if (q.includes(word))
        return problem(
          res,
          status,
          "Error",
          { code, detail: "English detail text." },
          status === 429 ? { "retry-after": "60" } : {},
        );
    }
    if (q.includes("busy429"))
      return problem(res, 429, "Too many requests", {}, { "retry-after": "2" });
    const answer = makeAnswer(q, ready, body.documentIds);
    if (demoActive(user))
      Object.assign(answer, {
        mode: "demo",
        model: "free/demo-model",
        notices: [PRIVACY, ...answer.notices.filter((n) => n !== PRIVACY)],
      });
    if (route === "/ask") {
      if (demoActive(user)) user.demo.used++;
      return json(res, 200, answer);
    }

    state.ask.started++;
    res.writeHead(200, {
      "content-type": "text/event-stream",
      "cache-control": "no-cache",
      "x-accel-buffering": "no",
    });
    let closed = false;
    res.on("close", () => {
      if (!res.writableEnded) {
        closed = true;
        state.ask.aborted++;
        state.ask.abortedQuestions.push(question);
      }
    });
    const send = (name, data) =>
      !closed && res.write(`event: ${name}\ndata: ${JSON.stringify(data ?? {})}\n\n`);
    const wait = (ms) => new Promise((r) => setTimeout(r, ms));
    const pause = q.includes("slow") ? 700 : 30;
    send("searching");
    await wait(pause);
    if (q.includes("hang")) {
      while (!closed) await wait(50); // until the client (through the BFF) goes away
      return;
    }
    if (q.includes("dropped")) {
      res.destroy();
      return;
    }
    send("tool_call", { tool: "search_documents" });
    await wait(pause);
    send("tool_call", { tool: "read_chunk" });
    await wait(pause);
    if (q.includes("ratelimit")) {
      send("error", {
        status: 429,
        title: "Too many requests",
        code: "demo_rate_limited",
        retryAfter: 3,
      });
      return res.end();
    }
    if (q.includes("timeout")) {
      send("error", { status: 504, title: "Timed out" });
      return res.end();
    }
    send("verifying");
    await wait(pause);
    if (demoActive(user)) user.demo.used++;
    send("answer", answer);
    state.ask.completed++;
    return res.end();
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
      hasLlmKey: Boolean(user.hasKey),
      demo: demoBody(user),
      chunks: {
        used:
          user.chunksUsed ??
          (user.docs ?? [])
            .filter((d) => d.status === "Ready")
            .reduce((n, d) => n + d.chunkCount, 0),
        limit: (user.hasKey ? LIMITS.byok : LIMITS.demo).chunks,
      },
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
