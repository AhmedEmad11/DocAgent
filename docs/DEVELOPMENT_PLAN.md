# Development Plan
## DocAgent: Document Q&A API with RAG and Agent Workflows

| Field | Value |
|---|---|
| Version | 0.2 (draft, awaiting approval) |
| Date | 2026-11-12 |
| Requirements | [SRS.md](SRS.md) |
| Progress log | [PROGRESS.md](PROGRESS.md) |

Rules for this project:
- No code is written until the SRS and this plan are approved.
- [PROGRESS.md](PROGRESS.md) is updated after every change.
- Open questions are asked, not assumed. A phase that depends on an open issue does not start until that issue is answered.
- Review checkpoints with the stakeholder after Phases 1, 4, 5 and 8 (PQ-3), and, once Phase 9 is approved, after sub-phases 9.1 and 9.7. Other phases continue without stopping, unless a new question comes up.

---

## 1. Technology Stack

| Area | Choice | Free? |
|---|---|---|
| Runtime | .NET 10 (LTS), ASP.NET Core Minimal APIs | Yes |
| Local orchestration | .NET Aspire (AppHost) | Yes |
| Database | PostgreSQL + pgvector; EF Core + Npgsql (`Pgvector.EntityFrameworkCore`) | Yes |
| Auth | ASP.NET Core Identity (bearer tokens) + Google + GitHub OAuth | Yes |
| Key encryption | ASP.NET Core Data Protection | Yes |
| LLM abstraction | Microsoft.Extensions.AI (`IChatClient`, `IEmbeddingGenerator`) | Yes |
| Agent framework | Microsoft Agent Framework | Yes (MIT) |
| Dev LLM | Ollama `gemma4:12b` via OllamaSharp | Yes |
| Demo / OpenRouter / OpenAI | OpenAI-compatible client (`Microsoft.Extensions.AI.OpenAI`) | Yes |
| Anthropic | Official `Anthropic` NuGet SDK (`IChatClient` support) | SDK free; usage paid by user |
| Gemini | Gemini OpenAI-compatible endpoint, or Google's .NET SDK (decided in Phase 5 after checking tool-calling support) | SDK free; usage paid by user |
| Embeddings | ONNX Runtime, in-process, `multilingual-e5-small` int8 (English + Arabic, 384 dims) | Yes |
| Email | SMTP via MailKit behind an `IEmailSender` interface. Dev: Mailpit container in the Aspire AppHost (web UI to read caught emails). Production: free-tier provider chosen in Phase 8 | Yes |
| Parsing | PdfPig (PDF), DocumentFormat.OpenXml (docx), Markdig (md) | Yes |
| Tokenizing/chunking | Microsoft.ML.Tokenizers + custom recursive splitter | Yes |
| Background work | `BackgroundService` + Postgres-backed job table | Yes |
| Rate limiting | ASP.NET Core rate limiting middleware | Yes |
| Observability | OpenTelemetry + Aspire Dashboard (dev) | Yes |
| Tests | xUnit, Testcontainers (Postgres + pgvector) | Yes |
| Hosting | SnapDeploy free tier (primary, no card; D43) + Neon (free tier); Azure Container Apps as documented alternative | Yes, within limits |
| CI | GitHub Actions on a public repository | Yes |
| Frontend (Phase 9) | Next.js + React + Tailwind CSS + shadcn/ui, English and Arabic (RTL), hosted on Vercel Hobby; see Phase 9 for versions | Yes (Hobby: non-commercial use) |

### Prerequisites on this machine
| Item | Status (checked 2026-11-12) |
|---|---|
| .NET 10 SDK | **Not installed**; Claude installs it in Phase 0 with Microsoft's `dotnet-install` script into `~/.dotnet` (no sudo) |
| Docker | Installed (29.9.2) |
| Ollama + `gemma4:12b` | Installed; tool calling supported |
| Git | Installed; the project folder is not yet a git repository |

---

## 2. Solution Structure (proposed)

```
dotnet/
├─ docs/                     SRS, plan, progress
├─ src/
│  ├─ DocAgent.AppHost/         Aspire orchestration (dev)
│  ├─ DocAgent.ServiceDefaults/ OpenTelemetry, health checks, resilience
│  ├─ DocAgent.Api/             Endpoints, auth, rate limiting, DI wiring
│  ├─ DocAgent.Core/            Domain models, interfaces, agent + tools
│  └─ DocAgent.Infrastructure/  EF Core, pgvector, parsers, ONNX embeddings, LLM providers
├─ tests/
│  ├─ DocAgent.UnitTests/
│  └─ DocAgent.IntegrationTests/ (Testcontainers)
└─ web/                      Next.js UI (Phase 9)
```

The repository will be public on GitHub (PQ-2).

---

## 3. API Surface (v1, draft)

| Method | Route | Purpose | SRS |
|---|---|---|---|
| POST | `/api/v1/auth/register` | Email/password sign-up (sends confirmation email) | AUTH-1, AUTH-7 |
| POST | `/api/v1/auth/login` | Email/password sign-in → bearer tokens | AUTH-2 |
| POST | `/api/v1/auth/refresh` | Refresh token | AUTH-6 |
| GET | `/api/v1/auth/confirmEmail` | Confirm email from the emailed link | AUTH-7 |
| POST | `/api/v1/auth/resendConfirmationEmail` | Resend the confirmation email | AUTH-7 |
| POST | `/api/v1/auth/forgotPassword` | Send a password-reset code | AUTH-7 |
| POST | `/api/v1/auth/resetPassword` | Set a new password with the reset code | AUTH-7 |
| GET/POST | `/api/v1/auth/manage/info`, `/manage/2fa` | Built into Identity API endpoints (not used by v1 requirements) | — |
| GET | `/api/v1/auth/external/{google\|github}?returnUrl=` | Start external sign-in (returnUrl must be allowlisted) | AUTH-3/4, D24 |
| GET | `/api/v1/auth/external/callback` | Internal: provider returns here; redirects to returnUrl with `?code=` or `?error=` | AUTH-5, D24 |
| POST | `/api/v1/auth/external/exchange` | Exchange one-time code for tokens | D24 |
| GET | `/dev/external-login-test` | Development-only page to try external sign-in | D24 |
| GET | `/api/v1/me` | Current user: id, email, confirmed, linked logins, has LLM key | AUTH |
| DELETE | `/api/v1/me` | Delete account and all data | AUTH-8 |
| POST | `/api/v1/documents` | Upload (multipart) | DOC-1..4 |
| GET | `/api/v1/documents` | List documents | DOC-6 |
| GET | `/api/v1/documents/{id}` | Document status/details | DOC-5 |
| DELETE | `/api/v1/documents/{id}` | Delete document | DOC-7 |
| POST | `/api/v1/ask` | Ask a question (optional document subset; optional SSE) | QA-1..5, 8..13 |
| POST | `/api/v1/documents/{id}/summary` | Summarize a document | QA-6 |
| POST | `/api/v1/documents/compare` | Compare two documents | QA-7 |
| GET | `/api/v1/me/llm-key` | Provider, model, last 4 chars | KEY-3 |
| PUT | `/api/v1/me/llm-key` | Save/replace key (validated) | KEY-1, 2, 4 |
| DELETE | `/api/v1/me/llm-key` | Remove key | KEY-4 |
| GET | `/api/v1/providers/{provider}/models` | Tool-capable models for the user's key | KEY-5 |
| GET | `/health/live`, `/health/ready` | Health checks | OBS-2 |

---

## 4. Phases

Each phase ends with: tests passing, PROGRESS.md updated, and a short review with you before the next phase starts.

### Phase 0: Environment and skeleton
**Depends on:** SRS + plan approved.
- Install the .NET 10 SDK with the `dotnet-install` script into `~/.dotnet` and add it to `PATH` (approved by stakeholder).
- `git init`, `.gitignore`, `.editorconfig`, README.
- Create the solution and projects from §2; Aspire AppHost with Postgres (pgvector image).
- ServiceDefaults: OpenTelemetry, health checks, HTTP resilience.
- CI workflow: build + test.
- **Done when:** `dotnet run` on the AppHost starts the API and Postgres; `/health/ready` is healthy; CI passes.

### Phase 1: Data layer and authentication
**Needs from stakeholder before starting:** Google and GitHub OAuth app credentials for local development (see [SETUP_CREDENTIALS.md](SETUP_CREDENTIALS.md)). Email needs no credentials in development (Mailpit).
- EF Core model: Identity tables, Document, Chunk (vector column + HNSW index), IngestionJob, UserLlmCredential.
- Migrations, plus enabling the pgvector extension.
- Identity bearer-token endpoints; Google and GitHub external login with automatic linking for provider-verified emails (AUTH-5).
- Mailpit added to the AppHost; email confirmation and password reset sent over SMTP to Mailpit (AUTH-7); unconfirmed accounts blocked from documents and Q&A.
- Per-user data filter (EF global query filter on `UserId`), as required by SEC-3.
- Auth rate limiting (SEC-8). Account deletion (AUTH-8).
- **Tests:** registration/login, external-login flow (mocked), tenant isolation.
- **Done when:** a user can register/sign in all three ways, confirm email, reset password, and call an authorized endpoint.
- **Checkpoint:** stakeholder review.

### Phase 2: Document ingestion
- Upload endpoint: content-type sniffing, size limits, per-user quotas (demo 3 docs / 5 MB; BYOK 100 docs / 25 MB).
- Parsers: PdfPig (page-aware), OpenXml (heading-aware), Markdig/plain text. Detect scanned PDFs (DOC-8).
- Chunker: target about 400 tokens, hard max 480, 60-token overlap (configurable), measured with the embedding model's own tokenizer. Reason: `multilingual-e5-small` reads at most 512 tokens including the `passage: ` prefix, so larger chunks would be silently truncated. Keeps page/section metadata.
- Full document text for summarize/compare (SRS §6 DocumentText) is derived from the ordered chunks; no separate text table.
- ONNX embedding generator (`multilingual-e5-small` int8) behind `IEmbeddingGenerator`, with the model's required `query:` / `passage:` prefixes. How the model files are shipped (in the image vs downloaded on first start) is a question for the stakeholder at the start of this phase.
- Arabic test documents (PDF, docx, md): check right-to-left text extraction quality.
- Background job worker with a Postgres job table; restart recovery (REL-3).
- Status, list and delete endpoints.
- **Tests:** each parser on sample files, chunk metadata, job state transitions, quota enforcement.
- **Done when:** all four file types, in English and Arabic, reach `Ready` with correct chunks and embeddings.

### Phase 3: Retrieval and basic Q&A
- Vector search (cosine, HNSW) filtered by user and optional document IDs; top-k with a score threshold.
- Optional hybrid search: Postgres full-text + vector, merged with reciprocal rank fusion (decided after measuring quality).
- Single-pass RAG `/ask` using Ollama `gemma4:12b`, with citations (QA-8) and "not found" handling (QA-9).
- **Tests:** retrieval isolation, citation mapping, answer-not-in-docs case.
- **Done when:** questions over uploaded documents return correct, cited answers locally.

### Phase 4: Agent workflows (Microsoft Agent Framework)
- Tools: `search_documents`, `get_chunk_context`, `get_document_outline`, `summarize_document`, `compare_documents`.
- Agent behavior: agentic search with re-query (QA-3); query decomposition (QA-4); verification pass (QA-5).
- Tool-call cap and timeout (QA-11: demo 4 / 60 s, BYOK 10 / 120 s); answer in the question's language (QA-14); prompt-injection safeguards (SEC-6): tools are scoped to the user's data whatever the document text says.
- Summary and compare endpoints (QA-6, QA-7); map-reduce summarization for long documents.
- Optional SSE streaming (QA-10).
- A small evaluation set of English and Arabic questions with expected answers/citations, run against `gemma4:12b`.
- **Done when:** the evaluation set passes at an accuracy level agreed with the stakeholder at the start of this phase.
- **Checkpoint:** stakeholder review.

### Phase 5: BYOK providers and key management
- Provider registry: OpenRouter, Anthropic, OpenAI, Gemini, each producing an `IChatClient`.
- Per-request `IUserChatClientFactory`: user key → BYOK client; no key → demo client (KEY-6).
- Key endpoints: validate with a low-cost call, encrypt with Data Protection, return only the last 4 chars (KEY-1..4).
- Model listing filtered to tool-capable models (KEY-5).
- Provider error mapping to clear Problem Details (KEY-7). Log/trace redaction (SEC-2).
- **Tests:** encryption round-trip, key never present in responses/logs, provider selection, error mapping (mocked HTTP).
- **Done when:** a real key works for each provider. Which providers the stakeholder can supply test keys for will be asked at the start of this phase; providers without a test key are verified with mocked HTTP only, and this is recorded in PROGRESS.md.
- **Checkpoint:** stakeholder review.

### Phase 6: Demo mode
- Demo is for signed-in users without a key only (D17).
- Choose the free OpenRouter model (and fallback) from the then-current list of free tool-capable models; record the choice in PROGRESS.md.
- Per-user daily quota of 10 demo questions, stored in the database so it survives restarts (DEMO-2); reduced agent limits (DEMO-3); fallback model on 429/unavailable (DEMO-4).
- Limit-reached and privacy messages (DEMO-5, PRIV-1).
- **Done when:** demo users are served by OpenRouter within limits and get clear messages when limits are hit.

### Phase 7: Hardening
- Security review (OWASP API Top 10, upload handling, key handling, tenant isolation).
- Load test of retrieval at the max allowed chunks per user (PERF-2).
- Resilience: retry/backoff policies (REL-2): one retry on 5xx/network errors for hosted providers, never on 429 (decision after Phase 7).
- Total chunk cap per user (20,000 BYOK / 3,000 demo), enforced during ingestion and shown in `GET /api/v1/me` (decision after Phase 7).
- OpenAPI completeness and examples; the document is served in all environments (anonymous, read-only), UI development-only.

### Phase 8: Deployment (SnapDeploy + Neon; Azure Container Apps as alternative)
**Hosting change 2026-11-21 (SRS D43, amends D8):** Azure requires a credit card, so the primary target is the **SnapDeploy free tier** (no card). Azure Container Apps stays a documented alternative; the Bicep and workflow already written for it are kept, not deleted. Free-tier facts that shape the work: 512 MB RAM, 0.25 vCPU, 100 container-hours per month for the whole account, sleep after 15 minutes idle with about 1 minute to wake, URL `https://docagent.containers.snapdeploy.app`, no custom domain on free, more than 100 requests per minute per IP gets 429 at SnapDeploy's edge.
- Stack on SnapDeploy: Neon free Postgres, Brevo SMTP (free tier), OpenRouter demo; secrets as SnapDeploy environment variables.
- **No pre-deploy hook** on SnapDeploy, so migrations run on startup (`MigrateOnStartup` enabled in production there). Keep migrations idempotent and quick; one instance only, so no migration race.
- **512 MB memory budget:** the ONNX embedding model plus .NET heap must fit with headroom. Measure resident memory after startup and during an upload and indexing run; cap concurrent indexing at 1, set `DOTNET_gcServer=0` and a conservative `DOTNET_GCHeapHardLimit`; if it does not fit, the fallback is the Azure alternative (needs a card) or a smaller model decision with the stakeholder.
- Cold start: expect about 1 minute after idle; the health check and the first-call UX allow for it (UI-GEN-7). The 100 container-hours per month are shared across the account, so do not run other containers on it.
- Dockerfile (multi-stage, includes the ONNX model).
- Neon project with pgvector; migrations run on startup (`MigrateOnStartup`) on SnapDeploy, as a deploy step on the Azure alternative.
- Deploy the image to SnapDeploy (GitHub-connected build or Docker image); environment variables set there. Azure alternative: Container Apps environment sized to stay within the free grant, secrets in Container Apps secrets (kept as documented option).
- Data Protection key ring persisted outside the container (location decided in this phase, e.g. stored in the database, encrypted).
- GitHub Actions: build → test → push image → deploy (SnapDeploy deploys from the connected repository or image; the Azure deploy job stays as the alternative).
- Register production OAuth redirect URIs with Google and GitHub.
- Choose the production email provider (free tier) with the stakeholder; Brevo SMTP chosen; its credentials stored as SnapDeploy environment variables (Container Apps secrets on the Azure alternative).
- **Done when:** acceptance criteria SRS §8 pass against the deployed service.
- **Checkpoint:** stakeholder review.

### Phase 9: Next.js frontend
**Status:** draft, awaiting approval together with [SRS §10](SRS.md). Requirements: UI-* IDs in SRS §10.3 to §10.7; decisions D33 to D39; open issues OI-8 to OI-23 (SRS §10.9), **all resolved 2026-11-21** (D40 to D43; the rest by accepting the SRS recommended defaults). Docs only so far: no frontend code is written until both are approved and the issues marked "Blocks" for a sub-phase are answered.

**Approach:** a Next.js backend-for-frontend (D35). The browser talks only to the Next.js app; the Next.js server holds the API tokens in an encrypted `httpOnly` cookie and calls the API server to server. Documents, keys, questions and answers are never stored by the UI (D38).

#### Project location and layout
`web/` in this repository (OI-18 resolved, D42), own CI job with path filters, independent of the .NET solution (`DocAgent.slnx` does not include it). Vercel project root directory: `web`. Design (OI-21, D42): neutral theme, one accent colour, text wordmark "DocAgent", no logo.
```
web/
├─ src/
│  ├─ app/[locale]/           pages: landing, sign-in, register, confirm, forgot/reset, documents, ask, settings, account, privacy
│  ├─ app/api/                BFF route handlers: auth callbacks (external), upload, ask stream, generic proxy for allowlisted routes
│  ├─ components/ui/          shadcn/ui components (copied in, RTL-checked)
│  ├─ lib/api/                typed API client (server only), ProblemDetails + error-code mapping
│  ├─ lib/session/            cookie encryption, refresh single-flight
│  ├─ i18n/ + messages/       next-intl config, en.json, ar.json
│  └─ middleware (proxy)      locale, CSP nonce, auth redirects
├─ tests/                     Vitest unit tests (colocated *.test.ts[x]) and Playwright e2e (e2e/)
├─ e2e/mock-api/              small mock of the API for e2e (see 9.0)
├─ .env.example, package.json, package-lock.json, next.config.ts, playwright.config.ts, vitest.config.ts
```

#### Stack and versions to pin
Versions below were the latest stable on npm on 2026-11-21 (`npm view <pkg> version`; Node v24.21.0 and npm 11.19.0 are installed on this machine). Exact versions are fixed in `package.json` and `package-lock.json` at 9.0 and re-checked then; Dependabot (or Renovate) keeps them current afterwards. `shadcn/ui` components are copied into the repository, not a runtime dependency.
| Area | Choice | Version found |
|---|---|---|
| Runtime | Node.js LTS (Vercel supports it; `.nvmrc` and `engines` pin the major) | 24.x (installed 24.21.0) |
| Framework | Next.js (App Router, route handlers, server actions) | 16.4.6 |
| UI library | React and React DOM | 19.3.1 |
| Language | TypeScript, strict mode (see risk R9.8 about the 7.x line) | 7.0.2 |
| Styling | Tailwind CSS v4 + shadcn/ui (CLI `shadcn`) | 4.3.5; shadcn CLI 4.23.0 |
| i18n | next-intl (routing, messages, ICU plurals, `Intl` formats) | 4.15.3 |
| Session | **`jose` 6.2.18 (JWE, `dir` + AES-256-GCM), decided in 9.1** over `iron-session` 9.1.0: GCM is what UI-SEC-1 names (iron-session seals with AES-CBC + HMAC), the sealed cookie is smaller (one base64 step; 2.3 KB for two 795-character tokens), and the same primitive seals the short-lived external-login state and pending-email cookies. No chunking (see A8 for sizes); sealing above 3,800 characters throws instead of losing the cookie. | jose 6.2.18 |
| Forms and validation | React Hook Form + Zod (shared by client form and server action); a 15-line zod resolver in `src/lib/forms` instead of `@hookform/resolvers`; `server-only` 0.0.1 marks server modules | 7.92.0; 4.6.7; server-only 0.0.1 |
| Unit tests | Vitest + Testing Library (React) + jsdom/happy-dom | Vitest 5.1.1; @testing-library/react 16.3.3 |
| E2E tests | Playwright (`@playwright/test`) with `@axe-core/playwright` | 1.65.1; axe version pinned at 9.0 |
| Lint/format | ESLint + `eslint-config-next`, Prettier | ESLint 10.15.0 |
| Markdown for answers | `react-markdown` + `rehype-sanitize` (no raw HTML) | to be pinned at 9.0 |
| Icons | `lucide-react` (used by shadcn/ui) | to be pinned at 9.0 |
| Fonts | `next/font` self-hosted: Inter (or Geist) + Noto Sans Arabic (OI-21 may change) | via Next.js |

#### Environment variables
| Variable | Where | Purpose |
|---|---|---|
| `DOCAGENT_API_URL` | Vercel (server only), `.env.local` | Base URL of the API, e.g. `https://localhost:7090` locally, the SnapDeploy URL (`https://docagent.containers.snapdeploy.app`) in production. |
| `SESSION_SECRET` | Vercel (server only; Sensitive), `.env.local` | 32+ random bytes for cookie encryption; rotated by changing it (signs everyone out). Optional `SESSION_SECRET_PREVIOUS` for rotation. |
| `NEXT_PUBLIC_SITE_URL` | Vercel, `.env.local` | Public origin of the UI (for OAuth return URL, canonical links, Origin check). Not a secret. |
| `BFF_SHARED_SECRET` | Vercel (server only), API (`Bff:SharedSecret`) | Confirmed (OI-10, D41): authenticates the forwarded client IP. |
| `UPLOAD_TICKET_*` | server only | Confirmed (OI-8, D40): signing/ticket settings for the upload flow. |
| `E2E_*` | CI only | Mock API URL and test accounts for Playwright. |
No secret is ever `NEXT_PUBLIC_*`; `.env.example` has placeholders only (UI-SEC-9).

#### API-side changes needed
The BFF removes most client-side API concerns. What changes in `src/DocAgent.Api` (each is a separate, small, tested change in its own commit, done in the sub-phase that needs it; every one needs stakeholder approval because the API is already accepted):
| # | Change | Needed for | Required? |
|---|---|---|---|
| A1 | **CORS: none.** The BFF is server to server, so `Cors:AllowedOrigins` stays empty in production (D39). Development keeps `http://localhost:3000` for the API's own test page only. Exception: A3 (upload) adds a narrow CORS rule for that one route (D40). | all | Only A3's rule |
| A2 | **`Authentication:External:AllowedReturnUrls`**: add the UI callback URL (exact path, e.g. `https://<project>.vercel.app/api/auth/external/callback`) in the production API settings (SnapDeploy environment variables; `infra/main.bicepparam` and the deploy workflow on the Azure alternative); local `http://localhost:3000/api/auth/external/callback` in `appsettings.Development.json` (**done in 9.1**; it replaced the broader `http://localhost:3000` prefix, so only the callback path is allowed). The validator already accepts a query string, so the login-CSRF nonce rides in `returnUrl` (no code change). Also register nothing new with Google/GitHub: their redirect URI stays the API's `/api/v1/auth/external/callback`. | 9.1, 9.7 | Configuration only |
| A3 | **Upload ticket** (OI-8 (b), **confirmed 2026-11-21, D40**): `POST /api/v1/documents/upload-ticket` returns a short-lived (about 60 s), single-use ticket bound to the user and to `POST /documents`; `POST /documents` also accepts that ticket in a header; a CORS policy for the UI origin on that route only; full DOC-3 limits and the same rate limits. Interim 4 MB UI cap only if 9.2 is built before A3 lands. | 9.2 | **Confirmed (D40)** |
| A4 | **Client IP trust** (OI-10 (a), **confirmed 2026-11-21, D41**): the API accepts `X-Client-IP` only together with a matching `Bff:SharedSecret`, sent by the BFF in the `X-Bff-Secret` header (compared in constant time; a secret shorter than 16 characters stops startup; unset = feature off), and uses it for the auth rate limit partition (**done in 9.1**: `TrustedClientIp`, `AuthRateLimiting`); ForwardedHeaders stays as is (SnapDeploy ingress; verify the hop count at 9.7). | 9.1 | **Confirmed (D41)** |
| A5 | **Email confirmation link to the UI** (OI-9): `Email:ConfirmationUrlBase`; when set, the emailed link is `{base}?userId=..&code=..` and opens the UI page `/confirm` (locale-less; the proxy adds the locale), where a button (a POST, so link scanners cannot use up the code) calls `confirmEmail` through the BFF. Development: `http://localhost:3000/confirm`; an invalid base stops startup. **Done in 9.1** (`ConfirmationLink`). | 9.1 | Recommended; accepted by OI-9 |
| A6 | **Cookie domain: none.** The API sets no cookies the UI relies on (bearer tokens only; the short-lived external-login cookie lives on the API host and is used only during the redirect round trip). The UI cookie is host-only on the Vercel domain (`__Host-` prefix). | all | No change |
| A7 | `AllowedHosts` is the API's own host and is unaffected by the UI. `Referrer-Policy` and header defaults on the API already fit. | all | No change |
| A8 | Verified at 9.1 (**by code reading, the existing integration tests and a measurement; Docker was not running, so not against the live API; `IdentityContractTests` pins these facts and runs with the other integration tests**): (1) **Register errors** are a validation problem (400, `title` "One or more validation errors occurred.", `errors` keyed by Identity error code: `PasswordTooShort`, `PasswordRequiresNonAlphanumeric`, `PasswordRequiresDigit`, `PasswordRequiresLower`, `PasswordRequiresUpper`, `PasswordRequiresUniqueChars`, `InvalidEmail`, `DuplicateUserName` + `DuplicateEmail`); a duplicate address **is** disclosed by the API (the UI shows one generic message). Reset: `InvalidToken` for a wrong code or unknown email. (2) **Unconfirmed accounts can sign in** (`RequireConfirmedEmail=false`): `/login` returns 200 and the lock shows later (403 on data endpoints, `emailConfirmed=false` on `/me`); `/login` failure is 401 with `detail` `Failed` or `LockedOut` (5 failures lock the account); `/confirmEmail` is 200 text or 401 with no body. (3) **`/refresh` returns a new access and a new refresh token but does not revoke the old one** (stateless Data Protection tickets), so concurrent refreshes cannot invalidate each other; the 14-day lifetime restarts at each refresh. (4) **Cookie size:** access and refresh tickets are about 795 characters each (measured with the bearer scheme and Identity's claims; a real account has a few more claims); sealed together 2.3 KB, under the 4 KB limit, so no chunking; the BFF refuses to seal above 3,800 characters. Sign-out cannot revoke tokens (no API endpoint); they never leave the server and expire on their own. | 9.1 | Verification |

#### Testing approach
- **Unit (Vitest + Testing Library):** error-code mapping, session sealing and refresh single-flight, Origin/CSRF check, SSE parser and stream reducer, API client against a fake `fetch`, form validation, formatters (sizes, dates, plurals) in `en` and `ar`, components in both directions.
- **E2E (Playwright):** two layers. (1) **Against a mock API** (a small Node server in `web/e2e/mock-api/` that implements the endpoints of SRS §10.5 with fixtures for every error code and SSE script). Fast, deterministic, runs in CI on every pull request, and is the only layer previews use. (2) **Against the real API** locally (`dotnet run` AppHost with Mailpit, a fake LLM or Ollama) for a small smoke set: register, confirm via Mailpit, upload, ask. Run by hand before each checkpoint and optionally in a nightly CI job; it needs Docker and the embedding model, so it is not in the per-PR job. Recommendation: mock layer in CI from 9.0, real-API smoke from 9.1.
- **Accessibility:** axe checks inside the e2e runs for each page in `en`/`ar`, light/dark; manual keyboard and screen-reader pass at 9.6.
- **Security tests:** a test that crawls all BFF responses of an e2e session and asserts no token substring; Origin rejection test; CSP header test; cookie flag test.
- **Visual/RTL checks:** Playwright screenshots of key pages in `ar` reviewed by a person (no automatic image baselines in v1, to keep it low maintenance).

#### CI job
A new job `web` in `.github/workflows/ci.yml` (path-filtered to `web/**` and the workflow itself so .NET-only changes do not run it; `build-and-test` is unchanged): Node from `web/.nvmrc`, cache `~/.npm`, then `npm ci`, `npm run lint`, `npm run typecheck`, `npm test` (Vitest), `npm run build`, `npx playwright install --with-deps chromium`, `npm run e2e` against the mock API, `npm audit --omit=dev --audit-level=high`. Lighthouse CI is added in 9.6 (budgets UI-PERF-1/2, run on the Vercel preview or a local production build). Vercel's own build runs the same `npm run build` on each push; branch protection requires the `web` job when `web/**` changed.

#### Sub-phases
Each ends with: tests passing, PROGRESS.md updated at the commit, no secrets committed. Only 9.1 and 9.7 stop for the stakeholder (suggested); the others continue unless a new question comes up (as in the rules at the top).

**9.0 Skeleton, tooling and CI**
- Stakeholder input: none outstanding (OI-16, OI-18, OI-21 resolved).
- Tasks: create `web/` with Next.js, TypeScript strict, Tailwind v4, shadcn/ui init (RTL-aware tokens, logical properties); next-intl with `en` and `ar` routing and message catalogs; fonts via `next/font`; app shell, landing and privacy placeholders; ESLint, Prettier, Vitest, Playwright, axe; mock API skeleton; `.env.example`, `.nvmrc`, `.gitignore` additions; CI job `web`; CSP, security headers and nonce middleware (UI-SEC-4) from the first page; update README with "run the UI".
- Verify and record (they feed later sub-phases): Vercel Hobby function duration limit with streaming (UI-PERF-4), request body limit (OI-8), `npm view` versions pinned.
- Tests: unit tests of the i18n setup (missing-key check script), a Playwright smoke test that `/en` and `/ar` render with `dir` set, header test.
- **Done when:** `npm run dev` shows the shell in English and mirrored Arabic; CI `web` job is green; versions pinned in the lockfile.

**9.1 Authentication and BFF (UI-AUTH-1 to 10, UI-SEC-1 to 3, 5, 10, UI-GEN-6)**
- Stakeholder input: none outstanding (A4 confirmed, A5 per OI-9 default); Arabic string review at this checkpoint (OI-22).
- Tasks: session module (sealed cookie, flags per UI-SEC-2, chunking only if A8 shows it is needed); refresh single-flight; API client with ProblemDetails parsing; generic allowlisted proxy route (UI-SEC-7); Origin/CSRF check (UI-SEC-3); register, confirm-email-sent, resend, sign-in, forgot, reset pages (React Hook Form + Zod, server actions); external login start and callback routes with the nonce and the code removed from the URL (UI-SEC-3, 5); auth redirects and `next` handling (UI-AUTH-9, UI-SEC-10); sign-out; `GET /me` loader; API changes A2, A4, A5 (if approved) with tests on the API side; error-code messages for auth (§10.6).
- Tests: unit (cookie seal/unseal, expiry, refresh race, Origin check, open-redirect check); e2e on the mock API for every auth path incl. all `?error=` values; real-API smoke: register, confirm (Mailpit), sign in, sign out, Google/GitHub using the dev OAuth apps; a test that proves no token reaches the browser (UI-SEC-1).
- **Done when:** a user can register, confirm, sign in (email, Google, GitHub), stay signed in across an access-token expiry, reset a password and sign out, in both languages, with no token visible to the browser.
- **Checkpoint:** stakeholder review (security design, Arabic auth strings, look and feel).

**9.2 Documents (UI-DOC-1 to 10, UI-ACC-4)**
- Stakeholder input: none outstanding (OI-8 resolved: upload ticket, D40).
- Tasks: documents list with status polling and back-off (UI-DOC-4, UI-PERF-5); upload with drag and drop, per-file progress and tier limit checks (via `GET /me` `hasLlmKey`); upload via the A3 ticket flow: the BFF requests the ticket, the browser posts the file directly to the API upload endpoint (CORS only there), no token in the browser (a 4 MB interim cap with a clear message only if 9.2 lands before A3); failed/notice display; delete dialog; document-count and chunk meters; empty/loading/error states.
- Tests: unit (limit logic, polling hook, file-type checks, name rendering in LTR/RTL); e2e on the mock API (every status, 409/413/415, failed reason, notice, delete); real-API smoke for each file type.
- **Done when:** all four statuses, limits and notices display correctly; uploads of the allowed sizes work (per D40); no token or file content is stored by the UI.

**9.3 Ask, streaming and citations (UI-QA-1 to 12)**
- Needs from stakeholder first: OI-11, OI-12.
- Tasks: SSE relay route (no buffering, abort propagation, `maxDuration` per UI-PERF-4); SSE parser and state machine (searching, tool_call, verifying, answer, error); ask page with scope selection; answer view with sanitized Markdown, inline `[n]` markers linked to citations, `notFound`, `incomplete`, mode/provider/model, notices; cancel; retry keeping the question; `dir="auto"` handling (UI-I18N-6); tab-only conversation list (if OI-12 default).
- Tests: unit (SSE parser with split chunks, reducer, citation-marker linking, Markdown sanitizer against injected HTML/links); e2e with a scripted mock stream (normal, not-found, incomplete, error event mid-stream, abort); real-API smoke with a local model.
- **Done when:** a streamed question shows progress and a cited answer in English and Arabic; cancel stops server-side work; every stream error has its message.

**9.4 Summary and compare (UI-SUM-1 to 4)**
- Tasks: summary dialog/page per document with optional focus; compare page with two selectors; working state with cancel; shared answer view; demo cost note.
- Tests: unit (same-document guard, focus length); e2e (success, 404/409, demo quota, timeout 504).
- **Done when:** summary and compare return cited results in the UI and all their error states are covered.

**9.5 Keys, demo banner and account (UI-KEY-1 to 6, UI-DEMO-1 to 4, UI-ACC-1 to 3)**
- Needs from stakeholder first: OI-13.
- Tasks: settings page (view, save/replace, delete); model list loading with the key in a header from the BFF only (never in a URL or log); key-field handling (UI-KEY-5); demo banner and privacy notice with live allowance and countdown; disabled controls at 0 remaining; account page with storage meter and delete-account flow.
- Tests: unit (banner states, countdown, key never logged: a test spies on logger and storage; model-list error fallback); e2e (save valid/invalid/no-credit/unknown model, replace, delete, demo to BYOK transition, quota exhaustion, account deletion); real-API smoke with a provider key if the stakeholder supplies one (as in Phase 5, otherwise mocked).
- **Done when:** a demo user sees and is limited by the banner, a key can be managed end to end, and the account can be deleted from the UI.

**9.6 Internationalization polish, accessibility and performance (UI-I18N-*, UI-A11Y-*, UI-PERF-1, 2, UI-GEN-5)**
- Stakeholder input: Arabic translation review (OI-22 accepted: at 9.1 and before 9.7); OI-14, OI-15 resolved by defaults.
- Tasks: RTL audit of every page and shadcn/ui component (menus, dialogs, toasts, tables, progress); Arabic typography and plural forms; mixed-direction content checks; dark theme and contrast tokens; focus management, live regions, error summaries, reduced motion, 320 px and 200% zoom checks; axe in CI for all pages x languages x themes; Lighthouse CI budgets; bundle analysis; manual keyboard and screen-reader pass (recorded in PROGRESS.md).
- Tests: axe zero serious/critical; screenshot review of Arabic pages; Lighthouse budgets; message-key parity check.
- **Done when:** acceptance criteria 7 and 8 (SRS §10.7) are met locally.

**9.7 Vercel deployment**
- Stakeholder input: none outstanding (OI-17, 19, 20, 23 resolved by defaults). Dependency: the Phase 8 deploy on SnapDeploy (`https://docagent.containers.snapdeploy.app`, no card needed) must exist to test against a real API; the 'needs Azure' dependency is removed. Until then this sub-phase can only be prepared and tested against the mock or a local API through a tunnel.
- Tasks: Vercel project (root `web`, Node 24, build `npm run build`), connect to GitHub; environment variables in Vercel (Production and Preview separately; secrets marked Sensitive); production `SESSION_SECRET`; API settings: `AllowedReturnUrls` (A2) as SnapDeploy environment variables (and `infra/main.bicepparam`/deploy workflow on the Azure alternative), `Bff:SharedSecret` (A4), email link base (A5); register nothing new at Google/GitHub; preview deployments limited per OI-23; custom domain if chosen; `docs/DEPLOYMENT.md` gets a "UI on Vercel" section (accounts, variables, rollback by promoting a previous deployment, cost = free); smoke test through the public URL against the SnapDeploy API (register, sign in, upload, ask), including waking the sleeping API (about 1 minute) and watching for 429 from SnapDeploy's per-IP limit (R9.14); security header and cookie verification on the deployed site; Lighthouse run on the deployed URL.
- Tests: the e2e smoke set pointed at the deployed UI and API with a throwaway account (deleted afterwards through the UI, which also tests UI-ACC-3).
- **Done when:** SRS §10.7 acceptance criteria 1 to 10 pass on the deployed site within free tiers.
- **Checkpoint:** stakeholder review (final).

#### Risks (Phase 9)
| # | Risk | Impact | Mitigation |
|---|---|---|---|
| R9.1 | Vercel function body limit (about 4.5 MB) below the upload limits | Uploads of 5 to 25 MB fail | Upload ticket (A3, D40; decided); a 4 MB interim cap only if 9.2 lands first; verified in 9.0 |
| R9.2 | All traffic comes from Vercel IPs, so the API's per-IP auth limit hits every user together | Sign-in fails for everyone after 10 attempts per minute | Forward the client IP with a shared secret (A4, D41; decided) |
| R9.3 | Vercel Hobby function duration and streaming limits vs the 120 s BYOK agent timeout | Long questions cut off | Verify at 9.0; set `maxDuration`; the API timeout stays lower than the platform's; message for 504 |
| R9.4 | Sealed session cookie over 4 KB (two tokens) | Sign-in appears to work but the cookie is dropped | Measure in 9.1 (A8); chunked cookies via `jose` as fallback |
| R9.5 | Refresh-token rotation with concurrent requests (two tabs refresh at once) | Random sign-outs | Single-flight per session; on refresh failure re-read the cookie once before signing out; test it |
| R9.6 | External login code travels in a URL and is exchanged server-side | Leak via logs/referrer; login CSRF | Single-use short-lived code (API), `Referrer-Policy: no-referrer`, immediate redirect, nonce state (UI-SEC-3, 5) |
| R9.7 | SnapDeploy sleeps the API after 15 minutes idle; wake takes about 1 minute | Looks broken | UI-GEN-7 retry/wait message with automatic retry; first-call timeout of at least 90 s; the 9.7 smoke test covers a cold wake |
| R9.8 | TypeScript 7.x (native compiler line) or Next.js 16.x and Tailwind 4.x may not all be compatible, or plugins lag | Build errors | Pin at 9.0 after a working build; fall back to the previous TypeScript major if a tool does not support it |
| R9.9 | Arabic/RTL defects in shadcn/ui components (they are LTR-first) | Poor Arabic UX | Logical properties, RTL review of every component used (9.6), Arabic screenshots reviewed by an Arabic speaker |
| R9.10 | Vercel Hobby is non-commercial and has usage caps; the plan could change | Forced upgrade or takedown | D34 accepted; UI is plain Next.js and can move to another host (for example Azure Container Apps) with configuration changes |
| R9.11 | Prompt-injected or malicious text in answers (Markdown links, HTML) | XSS, phishing links | Sanitized Markdown, no raw HTML, link allowlist (UI-SEC-8), strict CSP |
| R9.12 | The API's English-only messages in the Arabic UI | Inconsistent language | OI-13; translate by code; keep a list of API strings to translate |
| R9.13 | Scope: i18n, RTL and a11y roughly double the UI effort | Phase overruns | Done per sub-phase (RTL from 9.0, not at the end); 9.6 is polish and verification, not first-time work |
| R9.14 | SnapDeploy returns 429 at its edge above 100 requests per minute per IP (per app); all BFF calls come from Vercel's shared IPs, so one busy period could throttle every user | Users see rate-limit errors although the API's own limits are not hit | Keep BFF calls lean: no polling faster than UI-PERF-5 back-off, no per-render fan-out, batch where the API allows, cache `GET /me` per request; map 429 to the retry message; test the request budget of each page in 9.7 and measure real calls per minute; if it bites, ask SnapDeploy or move to the Azure alternative |
| R9.15 | The 100 container-hours per month are shared account-wide and the container uses 512 MB / 0.25 vCPU | Hours run out late in the month; slow embeddings; out-of-memory kill | Sleep-on-idle keeps hours low; memory budget measured in Phase 8; indexing concurrency 1; usage check before demos |

---

## 5. Risks

| Risk | Impact | Mitigation |
|---|---|---|
| `gemma4:12b` and the demo model handle tools differently from BYOK models | Inconsistent agent behavior | Evaluation set run against each model; simple, strict tool schemas; capped loops |
| Free OpenRouter models withdrawn or rate limited | Demo outage | Model IDs in config; fallback model; clear user messages |
| Neon free storage (0.5 GB) fills up | Uploads fail | Per-user quotas (D19); store only text + embeddings; storage monitoring |
| Production email provider limits or account problems | Confirmation/reset emails stop | Email sender behind an interface so a provider can be swapped by configuration |
| Arabic PDF text extracted in wrong order | Poor Arabic answers | Test in Phase 2; report results before continuing |
| SnapDeploy free-tier limits (512 MB RAM, 0.25 vCPU, 100 container-hours per month, sleep after 15 min, per-IP 429) | Out-of-memory, hours exhausted, slow wake, throttling | Memory budget measured in Phase 8, sleep-on-idle, lean BFF calls (R9.14); Azure Container Apps (needs a card) remains a documented alternative, with its own free-grant budget alert |
| Prompt injection inside uploaded documents | Data leakage / misuse | Tools hard-scoped to the user; document text marked as untrusted in prompts |
| Embedding model change later | Full re-embedding required | Model fixed now (multilingual-e5-small); store model name per chunk |
| Leaked user API keys | User financial loss | Encryption at rest, separate key ring, strict redaction, tests |

---

## 6. Planning Questions
Resolved 2026-11-12:
| ID | Question | Resolution |
|---|---|---|
| PQ-1 | Project/solution name? | DocAgent |
| PQ-2 | Public or private repository? | Public |
| PQ-3 | Review checkpoints? | After Phases 1, 4, 5 and 8 |

Questions deferred to the start of their phase (they need information that only exists then):
| Phase | Question |
|---|---|
| 1 | Google and GitHub OAuth app credentials (stakeholder supplies) |
| 8 | Which free-tier email provider to use in production |
| 2 | Ship the ONNX model inside the image, or download it on first start? |
| 4 | Accuracy level the evaluation set must reach |
| 5 | Which providers the stakeholder can provide test keys for |
| 6 | Which free OpenRouter model(s) to use (picked from the then-current list) |
