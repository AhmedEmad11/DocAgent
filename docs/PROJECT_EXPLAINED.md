# DocAgent explained: what it is, how it works, and why each decision was made

This document explains the whole project in one place: what DocAgent does, how the pieces fit together, and the reasoning behind every significant decision. The formal sources are the [SRS](SRS.md) (requirements and the decision log D1–D43), the [development plan](DEVELOPMENT_PLAN.md), the [security review](SECURITY_REVIEW.md), the [deployment guide](DEPLOYMENT.md) and the [progress log](PROGRESS.md). Decision numbers (D…) and open-issue numbers (OI-…) below point into the SRS.

---

## 1. What DocAgent is

DocAgent is a public web service where a signed-in user uploads documents (PDF, Word, text, Markdown) and asks questions about them in English or Arabic. Answers come from a language model, but only from the user's own documents, and every claim carries a citation back to the passage it came from. Beyond single questions it can summarise a document and compare two documents.

It is built as a portfolio-grade, production-shaped system with one hard constraint: **it must cost nothing to run** (D16). Every technology choice below was filtered through that constraint first.

Live deployment:
- UI: https://doc-agent-gilt.vercel.app (Vercel Hobby)
- API: https://docagent-a6971.containers.snapdeploy.app (SnapDeploy free tier; OpenAPI at `/openapi/v1.json`)
- Database: Neon free Postgres with pgvector
- Source: https://github.com/AhmedEmad11/DocAgent

---

## 2. How the project was run

| Decision | Why |
|---|---|
| **Docs first**: SRS, plan and progress files before any code; open questions are asked, never assumed. | A multi-phase project with many external services drifts quickly. Writing requirements and decisions down first made every later choice traceable and let the stakeholder approve scope before effort was spent. |
| **Phased delivery** (Phase 0 skeleton → 9.x frontend) with stakeholder checkpoints after Phases 1, 4, 5, 8, 9.1 and 9.7. | Each phase ends in a working, tested state. Checkpoints sit where decisions are expensive to undo (auth, agent quality, key handling, deployment, UI auth, final UI). |
| **Progress log updated only at commits**, one entry per commit. | Keeps the log honest (it describes what is in git) and cheap to maintain. |
| **Push after every commit**; CI on every push. | The public repo is always current and every commit is independently verified (build, tests, Docker build, infra validation, web job). |
| **API first, UI second** (D2). | The API is the product; the UI is one client. Finishing the API first fixed the contract the UI builds against. |

---

## 3. Architecture at a glance

```
Browser ──► Next.js UI on Vercel (BFF: holds the session, never exposes tokens)
               │  server-to-server HTTPS, X-Client-IP + X-Bff-Secret
               ▼
         ASP.NET Core 10 API on SnapDeploy (512 MB, 0.25 vCPU)
           ├─ Identity (email/password, Google, GitHub), JWT-style bearer tokens
           ├─ Ingestion worker: extract → chunk → embed (ONNX, in-process)
           ├─ Retrieval: pgvector HNSW + Postgres full-text, fused with RRF
           ├─ Agent (Microsoft Agent Framework): tools + answer verifier
           └─ LLM providers: Ollama (dev) / OpenRouter demo / BYOK
               ▼
         Neon Postgres + pgvector        Brevo SMTP (email)
```

.NET solution layout:
- `DocAgent.Core`: entities, abstractions, retrieval contracts, auth helpers. No infrastructure dependencies.
- `DocAgent.Infrastructure`: EF Core data layer and migrations, ingestion, embeddings, retrieval, agents, LLM providers, email.
- `DocAgent.Api`: Minimal API endpoints, auth, security hardening, OpenAPI, migrations runner.
- `DocAgent.AppHost` / `DocAgent.ServiceDefaults`: .NET Aspire for local orchestration (Postgres, Mailpit, Ollama) and shared telemetry/health defaults.
- `web/`: the Next.js UI (D42).
- Tests: unit, integration (Testcontainers with real Postgres + pgvector), and an evaluation suite for answer quality.

---

## 4. Platform and core technology

| Decision | Why |
|---|---|
| **.NET 10 (LTS)** with ASP.NET Core Minimal APIs (D10). | Long-term support release, strong performance in small containers, first-class OpenAPI and Identity. Minimal APIs keep endpoints small and explicit. |
| **.NET Aspire** for local development. | One command starts the API with Postgres, Mailpit and Ollama wired together, matching production topology without hand-written compose files. |
| **PostgreSQL + pgvector** for everything: users, documents, chunks and vectors (D5). | One free database instead of a separate vector store. Transactions cover documents and their vectors together, and tenant filters apply to vectors the same way they apply to rows. |
| **Neon** free tier as the production database (D8). | Free, managed Postgres with pgvector, no credit card. |
| **EF Core + Npgsql**, migrations in code. | Typed queries, global per-user query filters for tenant isolation, and versioned schema changes. |

---

## 5. Documents and ingestion

| Decision | Why |
|---|---|
| Supported types: text-based PDF, DOCX, TXT, MD (D3). | Covers most real documents without OCR, which would need heavy native dependencies and break the free/512 MB budget. Scanned PDFs are reported as "no text found" rather than silently failing. |
| **Original files are not kept**; only extracted text and chunks are stored (D15). | Less sensitive data at rest, far less storage on a free database, and deletion is simple. The trade-off (no re-download of originals) is accepted. |
| Ingestion runs in a **background worker with a durable job queue** in the database. | Uploads return immediately; long documents do not hold an HTTP request open. Jobs survive restarts: interrupted jobs are re-queued at startup, and after 3 interruptions they fail with a visible reason. On graceful shutdown (SnapDeploy sleep) the running job is released without counting an attempt. |
| Token-based **chunking with overlap**. | Chunks fit the embedding model's window, and overlap keeps sentences that straddle a boundary retrievable. |
| **Arabic PDF repair is best effort** (D31). | Many PDFs store Arabic in visual (reversed) order. The API detects and repairs it heuristically and flags the document with a notice, so users know the text may be imperfect. Arabic DOCX/TXT/MD are fully supported. |
| Limits: demo 3 documents × 5 MB, BYOK 100 × 25 MB; total chunks demo 3,000, BYOK 20,000; max 2,000 chunks per document (D19, Phase 7). | Protects the free database (Neon's storage cap) and the 512 MB container from a single user exhausting shared resources. |

---

## 6. Embeddings

| Decision | Why |
|---|---|
| **In-process ONNX Runtime** with `multilingual-e5-small` (int8, 384 dimensions) (D9). | No paid embedding API and no extra service. The same model runs in development and production, so retrieval behaves identically. The model is multilingual, which English + Arabic requires. 384 dimensions keep vectors small in the free database. |
| The model is **downloaded at build time**, pinned to a revision and SHA-256 verified, never committed or fetched at runtime (D30). | Reproducible builds, no 100+ MB binary in git, and no runtime dependency on Hugging Face being reachable. |
| **Memory tuning for 512 MB** (SnapDeploy): batch size 4, ONNX memory arena and memory pattern off, glibc `malloc_trim` after loading and every 20 batches, low-memory model load, conservative .NET GC settings. | The free container has 512 MB. Before tuning, a medium document pushed the process to the limit. After tuning: idle about 75 MiB, a 400-chunk file peaks at about 395–484 MiB, the largest tested 23 MB DOCX runs at the limit without being killed. Turning the arena off costs about 30% speed; memory was chosen over speed. |

---

## 7. Retrieval

| Decision | Why |
|---|---|
| **Hybrid search**: pgvector HNSW (semantic) plus Postgres full-text search (keywords), fused with **Reciprocal Rank Fusion** (k = 60). | Semantic search alone misses exact codes, names and numbers; keyword search alone misses paraphrases. RRF combines the two rankings without tuning score scales. Defaults: top 8 results, 4× candidates from each source, about 3,000 tokens of context. |
| **HNSW index** on the vectors. | Fast approximate nearest-neighbour search that works well at the scale of a free database. |
| Every query is **scoped to the current user** by global query filters. | Tenant isolation (D1) is enforced in the data layer, not by remembering a `WHERE` clause in each query. The security review found no cross-tenant path. |

---

## 8. The agent and answer quality

| Decision | Why |
|---|---|
| **Microsoft Agent Framework** for the agent loop (D6). | The .NET-native agent library with tool calling and provider abstraction (`Microsoft.Extensions.AI`), so the same agent runs on Ollama, OpenRouter, OpenAI, Anthropic or Gemini. |
| Agent **tools**: search documents, get neighbouring passages, get a document outline, summarise a document. | Lets the model look further when the first search is not enough (multi-step questions, context around a passage) instead of answering from a single retrieval. |
| **Tool-call and time limits**: demo 4 calls / 60 s, BYOK 10 calls / 120 s (D20), enforced by a counting wrapper and a tool-limit chat client. | Bounds cost and latency, especially on the shared demo key; a looping model cannot run up a bill. |
| **Citations are mandatory**; sources are registered as tools return them, and answers reference them by number. | Users can check every claim. Citations that do not point to a real retrieved source are rejected. |
| **Answer verifier** with strict JSON output; it **fails closed**. | A second pass checks that the answer is supported by the cited passages. If the verifier cannot run or returns invalid output, the answer is marked as unverified instead of being shown as trustworthy. Chosen by the stakeholder over "fail open". |
| **"Not found" is a valid answer.** | The acceptance bar (D32) requires every unanswerable question to be answered as not found. Inventing an answer is worse than admitting the documents do not say. |
| Acceptance: ≥ 80% of answerable questions correct with a correct citation on an English + Arabic evaluation set run on `gemma4:12b` (D32). | A measurable quality gate, run locally on the free development model. |
| **No chat history** is stored; each question is independent (D14). | Simpler and more private; the UI keeps a conversation only in the open browser tab (OI-12). |
| Streaming answers over **Server-Sent Events** (search, tool call, verifying, answer). | Users see progress during the several seconds an agent run takes, and can cancel. |

---

## 9. Language models: development, demo and BYOK

| Decision | Why |
|---|---|
| **Development: local Ollama `gemma4:12b`** (D11). | Free, private, and supports tool calling, so the full agent can be developed and evaluated without any API key. |
| **Demo: a free OpenRouter model on the operator's key** (D12), Gemma 4 31B chosen. | Lets anyone try the service at no cost to them. The operator key is required at startup outside development so a misconfigured deployment fails loudly. |
| Demo requires sign-in (D17); **10 questions per user per UTC day** (D18) via an atomic database upsert; global demo throttle 20 requests/minute. | Free model quotas are shared; per-user and global limits stop one user or a script from exhausting them. Atomic upsert makes the counter correct under concurrent requests. |
| Demo **privacy notice** shown with demo answers. | Free third-party models may log prompts; users are told not to use the demo for confidential documents. |
| **BYOK (bring your own key)** for OpenRouter, Anthropic, OpenAI and Google Gemini (D7). | Heavy users pay their own provider directly; the operator's cost stays at zero. Gemini is used through its OpenAI-compatible endpoint. |
| BYOK keys are **encrypted with ASP.NET Data Protection** (purpose `DocAgent.UserLlmKey.v1` plus a per-user sub-purpose), never logged, sent only from host-pinned HTTP clients with header logging off. | A stolen database row is useless without the key ring, and a key can only be decrypted for its own user. Pinned hosts stop a key being sent anywhere except its provider. |
| `Llm:NoKeyRoute` = Auto / Ollama / Demo / None. | One setting decides what a user without a key gets in each environment (Ollama locally, the demo in production). |
| Provider retry: **one retry on 5xx or network error, never on 429**. | Transient failures recover; rate limits are respected instead of hammered. |

---

## 10. Accounts and authentication

| Decision | Why |
|---|---|
| **ASP.NET Core Identity** with email + password and **Google and GitHub** sign-in (D4). | Proven, well-audited identity stack; external sign-in lowers friction. |
| Default Identity password rules (D25); access token 1 h, refresh 14 days (D26). | Familiar rules; short-lived access tokens with a refresh token balance security and convenience. |
| **External sign-in hands tokens over with a one-time code** (D24), redirecting only to allowlisted return URLs. | Tokens never appear in URLs, browser history or logs; an attacker cannot redirect a code to their own site. |
| Auto-linking an external login to an existing account **only when the provider reports the email as verified** (D21); an unconfirmed password account that gets linked **loses its password** (D28). | Prevents account takeover by someone who pre-registers a victim's email with a password. |
| **Email confirmation and password reset** in v1 (D22); production email through **Brevo SMTP** (Gmail SMTP was dropped because app passwords were unavailable). | Required for a public service; Brevo's free tier needs no card. |
| If an email fails to send, the request still succeeds and the failure is logged without content (D29). | Users are not blocked by a transient mail outage and can request the email again. |
| Auth endpoints limited to **10 requests/minute per IP** (D27). | Slows password guessing and abuse of email sending. |

---

## 11. Security hardening (Phase 7)

Reviewed against the OWASP API Security Top 10; 31 findings, none allowing cross-tenant access (see [SECURITY_REVIEW.md](SECURITY_REVIEW.md)).

| Decision | Why |
|---|---|
| **Endpoint auth inventory test**: every endpoint must declare an explicit auth decision. | A new endpoint can never become public by accident. |
| 256 KB JSON body limit, per-user concurrency caps, chunk caps, demo throttles. | Resource-consumption attacks are the realistic threat on free tiers. |
| Security headers (CSP, HSTS, nosniff, no-referrer, frame denial), no `Server` header, CORS allow-list. | Standard defence in depth. |
| **Forwarded headers** trusted with a hop limit, plus `AssumeHttps` for edges that do not send `X-Forwarded-Proto`. | Behind SnapDeploy's Cloudflare edge the app must know the real client IP (rate limits) and that the request was HTTPS (HSTS, OAuth callback URLs). |
| `AllowedHosts` set to the exact public host. | Host-header attacks are rejected (a foreign Host gets 403). |
| **Data Protection key ring stored in the database, protected by a certificate** (`DataProtection__CertificatePfxBase64` + password). | Containers are disposable; keys must survive restarts and redeploys, and keys at rest must be encrypted. |
| OpenAPI published in all environments. | The API is public by design; publishing its contract helps clients and reviewers. |
| Secrets live only in user-secrets, a private local folder, or the hosting dashboards; never in git, chat or logs. Test passwords are obvious placeholders (`Example-Only-1`). | GitGuardian flagged old test passwords as false positives; replacing them with placeholders was the stakeholder's choice over silencing the scanner. |

---

## 12. Deployment

| Decision | Why |
|---|---|
| **Originally Azure Container Apps** (Bicep + GitHub workflow, scale to zero). | Production-grade, free allowance, scale to zero. |
| **Moved to SnapDeploy free tier** (D43), Azure kept as a documented alternative. | Azure sign-up requires a credit card; SnapDeploy does not. Cost: 512 MB / 0.25 vCPU, 100 container-hours a month, sleep after 15 minutes idle, about a minute to wake, 100 requests/minute per IP. |
| **Migrations at startup under a Postgres advisory lock** (`Database:MigrateOnStartup`). | SnapDeploy has no pre-deploy hook. The lock guarantees that two instances starting together migrate exactly once; a failed migration stops startup with a clear message. `--migrate` remains for hosts with a deploy step. |
| App listens on the platform's **`PORT`** variable, else 8080. | SnapDeploy assigns the port; Azure targets 8080. |
| **Dockerfile: 2 stages, under 3,000 bytes, builds for the host's CPU** (`TARGETARCH`). | Found during the first deploys: SnapDeploy's free tier rejects more than 2 stages or 3,000 bytes, and runs on arm64, so an x64 build would not load. CI now checks both limits. |
| Non-root container, pinned base images, health endpoints `/health/live` and `/health/ready`. | Standard container hygiene; the platform and smoke tests use the health checks. |

Lessons from the first deploy (recorded in the deployment guide): a failed start deletes the container and its variables on SnapDeploy; the container may get a name suffix (`docagent-a6971`), so `AllowedHosts` must match; long secret values must be copied from files to avoid truncation; SnapDeploy auto-deploys on every push to `main` and allows 5 deploys per 12 hours.

---

## 13. The frontend (Phase 9)

| Decision | Why |
|---|---|
| **Next.js (App Router), React, TypeScript, Tailwind CSS, shadcn/ui** (D33). | Mainstream, well-supported stack; server components and route handlers make the BFF pattern natural; shadcn/ui gives accessible components owned in the repo. |
| **Vercel Hobby** hosting (D34). | Free and built for Next.js. Hobby is non-commercial; accepted for this project. |
| **Backend-for-frontend (BFF)** (D35): the Next.js server holds the API tokens in an encrypted httpOnly cookie (`jose` JWE, AES-256-GCM, `__Host-` prefix, SameSite=Lax) and calls the API server to server. | Browser JavaScript never sees a token, so XSS cannot steal one. Refresh happens on the server, de-duplicated across concurrent requests. |
| External sign-in completed server side with a one-time nonce cookie (D36). | Protects against login CSRF and keeps the code out of browser JavaScript. |
| **Upload ticket** (D40): the BFF gets a single-use, 60-second, upload-only ticket; the browser sends the file straight to the API; CORS is open only on that one endpoint for the UI origin. | Vercel functions accept at most 4.5 MB request bodies, but BYOK uploads can be 25 MB. The ticket avoids routing files through Vercel without giving the browser a real token. |
| **Trusted client IP** (D41): the BFF sends `X-Client-IP` with a shared secret; the API trusts it only when the secret matches (constant-time comparison). | All UI traffic reaches the API from Vercel's IPs; without this, every user would share one rate-limit bucket. |
| No UI database (D38); the API's CORS stays closed except for uploads (D39). | The UI is a thin, stateless client; less to secure and nothing to back up. |
| **English and Arabic with full RTL** (D13, D37), Western digits, Noto Sans Arabic. | The service targets both audiences; RTL is designed in, not patched later. Arabic strings are first drafts pending a native review. |
| `web/` folder in the same repo, neutral look, text wordmark (D42). | One repo keeps API and UI changes in step; CI path filters run the web job only when the UI changes. |
| Answers rendered with sanitised Markdown; injected HTML and `javascript:` links are inert. | Model output is untrusted (prompt injection through documents). |
| Accessibility: axe checks on every page in both languages and themes, keyboard and focus handling, contrast fixes. | Accessibility is a requirement, verified automatically in CI. |

Known gaps: first-load JavaScript is 164–208 KB against a 150 KB target (mostly the form-validation library); no manual screen-reader pass yet; Lighthouse runs need the deployed URL; cold-start handling (the UI waking the sleeping API and showing a waiting notice, UI-GEN-7) is being built.

---

## 14. Testing and quality

| Layer | What it covers |
|---|---|
| Unit tests (.NET, about 430) | Chunking, RRF, prompts, limits, key encryption, auth helpers, upload tickets. |
| Integration tests (.NET, about 300) | Real Postgres + pgvector in Testcontainers: auth flows, tenant isolation, ingestion recovery, rate limits, production configuration, deployment behaviour. |
| Evaluation suite | English + Arabic question set against the agent (D32). |
| Web unit tests (Vitest, about 520) | Session sealing, refresh, proxy allowlist, SSE parser, error mapping, limits, the key-never-logged guarantee. |
| Web end-to-end (Playwright, about 170) | Every page and error state against a mock API, security headers, cookies, axe accessibility. |
| CI | Build with zero warnings, all tests, Docker build with size/stage limits, infrastructure validation, web lint/typecheck/i18n parity/build/e2e/audit. |

---

## 15. Costs

Everything runs on free tiers: SnapDeploy (API), Vercel Hobby (UI), Neon (database), Brevo (email), OpenRouter free model (demo), GitHub (code and CI), Ollama locally (development). The only costs in the system are the ones BYOK users choose to pay their own LLM provider.
