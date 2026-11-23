# Software Requirements Specification (SRS)
## Document Q&A API with RAG and Agent Workflows

| Field | Value |
|---|---|
| Version | 0.3 (§1–9 approved 2026-11-12; §10 frontend approved 2026-11-21) |
| Date | 2026-11-12 |
| Status | Approved |

---

## 1. Introduction

### 1.1 Purpose
This document specifies the requirements for a web API that lets users upload documents and ask natural-language questions about them. Answers are produced by retrieval-augmented generation (RAG) driven by an LLM agent, and every answer includes citations to the source text.

### 1.2 Scope
In scope for version 1:
- A REST API (ASP.NET Core, .NET 10) for accounts, document ingestion, question answering, and LLM key management.
- Agent workflows over the user's documents: agentic search, query decomposition, answer verification, and document summarize/compare.
- Bring-your-own-key (BYOK): users supply their own LLM provider key and pay for their own usage.
- A demo mode that runs on a free OpenRouter model using the operator's key, with strict limits.

Out of scope for version 1 (planned later, see the development plan):
- The Next.js web frontend. It will be built after the API is finished.

Explicitly excluded:
- Storing chat history (the API is stateless per question).
- Keeping original uploaded files.
- OCR of scanned PDFs or images.

### 1.3 Definitions
| Term | Meaning |
|---|---|
| RAG | Retrieval-augmented generation: retrieve relevant text chunks and give them to the LLM to answer from. |
| Chunk | A section of a document's extracted text (with metadata) that is embedded and stored for search. |
| Embedding | A numeric vector representing text meaning, used for similarity search. |
| Agent | An LLM that can call tools (e.g. search) in a loop to complete a task. |
| BYOK | Bring your own key: a user's own LLM provider API key. |
| Demo mode | Usage without a user key, served by a free OpenRouter model on the operator's key. |
| Provider | An LLM API vendor: OpenRouter, Anthropic, OpenAI, Google Gemini. |

### 1.4 Decisions recorded from the stakeholder
| # | Decision |
|---|---|
| D1 | Public multi-tenant service. Each user's documents are private to that user. |
| D2 | API first; a Next.js UI follows after the API is complete. |
| D3 | Supported file types: text-based PDF, Word (.docx), plain text (.txt), Markdown (.md). |
| D4 | Sign-in: email + password **and** external sign-in with Google and GitHub. |
| D5 | Vector storage: PostgreSQL + pgvector. |
| D6 | Agent framework: Microsoft Agent Framework. |
| D7 | BYOK providers: OpenRouter, Anthropic (Claude), OpenAI, Google Gemini. |
| D8 | Hosting: API on **SnapDeploy free tier** (primary); database on Neon (free tier, Postgres + pgvector). Originally Azure Container Apps; **changed 2026-11-21** because Azure requires a credit card (see D43). Azure Container Apps stays a documented alternative. |
| D9 | Embeddings run in-process with ONNX Runtime, using the same model in development and production. |
| D10 | Target framework: .NET 10 (LTS). |
| D11 | Development LLM: local Ollama with `gemma4:12b` (supports tool calling). |
| D12 | Demo LLM: a free OpenRouter model. OpenRouter on the operator's key is for the demo only. |
| D13 | Languages: English (primary) and Arabic, both in v1 (OI-5). |
| D14 | Chat history is not stored. Each question is independent. |
| D15 | Original files are not kept; only extracted text and chunks are stored. |
| D16 | The whole stack must be free to run (excluding users' own BYOK costs). |
| D17 | Demo mode requires sign-in; anonymous visitors cannot use it (OI-1). |
| D18 | Demo limit: 10 questions per user per day (OI-2). |
| D19 | Document limits: demo 3 documents of up to 5 MB each; BYOK 100 documents of up to 25 MB each (OI-3). Total chunks per user: demo 3,000, BYOK 20,000 (DOC-3). |
| D20 | Agent limits: demo 4 tool calls / 60 s timeout; BYOK 10 tool calls / 120 s timeout (OI-4). |
| D21 | External logins link automatically to an existing account with the same email, only when the provider reports the email as verified (OI-6). |
| D22 | Email confirmation and password reset are in v1 (OI-7). Development sends to a local Mailpit email catcher; the production email provider is chosen before deployment (Phase 8). Gmail SMTP was dropped because app passwords are not available on the stakeholder's account. |
| D23 | Product/solution name: **DocAgent**. |
| D24 | External sign-in hands tokens to the client with a **one-time code**: the API redirects to an allowlisted return URL with a short-lived, single-use code, and the client exchanges it for tokens. Tokens never appear in URLs. Dev allowlist: `http://localhost:3000` plus the built-in API test page at `https://localhost:7090/dev/external-login-test` (development runs over HTTPS). |
| D25 | Password rules: ASP.NET Core Identity defaults (min 6 chars; upper, lower, digit and symbol required). |
| D26 | Token lifetimes: access token 1 hour, refresh token 14 days. |
| D27 | Auth endpoint rate limit: 10 requests per minute per IP address (configurable). |
| D28 | When an unconfirmed email/password account is auto-linked to a verified Google/GitHub login, its password is removed (prevents account pre-registration takeover). The owner can set a password later via password reset. |
| D29 | If a confirmation or reset email fails to send, the request still succeeds; the failure is logged (without content) and the user can request the email again. |
| D30 | The ONNX embedding model is downloaded at build time by a script (pinned revision, SHA-256 verified) into a git-ignored folder locally and into the Docker image; it is not committed to git and not downloaded at runtime. |
| D31 | Arabic text extraction from PDFs is **best effort**: many PDFs store Arabic in reversed (visual) order and the API repairs it heuristically. When a PDF needed this repair, the document carries a notice saying Arabic text may be inaccurate. Arabic DOCX, TXT and MD are fully supported. |
| D32 | Phase 4 acceptance: on the English + Arabic evaluation set run against `gemma4:12b`, at least 80% of answerable questions are answered correctly with a correct citation, and every unanswerable question is answered as not found (no invented answers). |

---

## 2. Overall Description

### 2.1 Product perspective
A new, standalone backend service. Clients (first API consumers and Swagger, later the Next.js UI) call it over HTTPS.

```
Client ──HTTPS──► ASP.NET Core API (SnapDeploy; Azure Container Apps as alternative)
                   ├─ Identity (email/password, Google, GitHub)
                   ├─ Ingestion: parse → chunk → embed (ONNX, in-process)
                   ├─ Agent (Microsoft Agent Framework) ──► IChatClient
                   │                                         ├─ dev: Ollama gemma4:12b
                   │                                         ├─ demo: OpenRouter free model
                   │                                         └─ BYOK: OpenRouter / Anthropic / OpenAI / Gemini
                   └─ PostgreSQL + pgvector (Neon)
```

### 2.2 User classes
| Class | Description |
|---|---|
| Anonymous visitor | Not signed in. Can only register, sign in, confirm email and reset password. No demo access (D17). |
| Registered user without key | Signed in, has not added an LLM key. Uses demo mode. |
| Registered user with key (BYOK) | Signed in, has a validated provider key. Full limits; pays the provider directly. |
| Operator | Deploys and configures the service, owns the OpenRouter demo key. No admin UI in v1. |

### 2.3 Operating environment
- Runtime: .NET 10 in a Linux container.
- Production: SnapDeploy free tier (primary; Azure Container Apps as a documented alternative, D43) + Neon PostgreSQL with the pgvector extension.
- Development: local machine (WSL2 Linux) with Docker and Ollama.

### 2.4 Constraints
- C1: Every hosted component must fit a free tier without a credit card (SnapDeploy free tier, Neon free tier; originally Azure Container Apps free grant, changed 2026-11-21, D43).
- C1a: SnapDeploy free-tier limits that shape the design: 512 MB RAM, 0.25 vCPU, 100 container-hours per month for the whole account, sleep after 15 minutes idle with about 1 minute to wake, fixed URL `https://docagent-a6971.containers.snapdeploy.app` (no custom domain on free), and a limit of 100 requests per minute per IP at its edge (429 beyond it).
- C2: Neon free tier storage is limited (0.5 GB at the time of writing), so per-user document limits are required (D19). Only extracted text and embeddings are stored (originals are discarded), which is much smaller than the file sizes, but many BYOK users at the 100-document limit could still fill it; storage usage must be monitored.
- C6: The production email provider must have a free tier; its daily sending limit caps confirmation and reset emails. Chosen in Phase 8.
- C3: The embedding model must be identical in every environment; changing it requires re-embedding all chunks.
- C4: Free OpenRouter models have per-minute and per-day request limits, and individual free models can be withdrawn.
- C5: The chat model must support tool calling.

### 2.5 Assumptions and dependencies
No assumptions beyond the decisions in §1.4 are made. Dependencies: Ollama (dev), OpenRouter, the four BYOK provider APIs, Google and GitHub OAuth apps, Neon, SnapDeploy (Azure as alternative).

---

## 3. Functional Requirements

Priority: **M** = must have (v1), **S** = should have (v1 if time allows), **C** = could have (later).

### 3.1 Accounts and authentication (AUTH)
| ID | Requirement | Pri |
|---|---|---|
| AUTH-1 | Users can register with email and password. | M |
| AUTH-2 | Users can sign in with email and password and receive a token for API calls. | M |
| AUTH-3 | Users can sign in with Google. | M |
| AUTH-4 | Users can sign in with GitHub. | M |
| AUTH-5 | A Google/GitHub sign-in whose email matches an existing account is linked to that account automatically, only if the provider reports the email as verified; otherwise it is rejected with a clear message. | M |
| AUTH-6 | Users can sign out and tokens can be refreshed. | M |
| AUTH-7 | Email/password accounts must confirm their email before using documents or Q&A; users can request a password reset by email. Emails are sent over SMTP: Mailpit in development, a free-tier provider in production (chosen in Phase 8). | M |
| AUTH-8 | Users can delete their account; this deletes their documents, chunks, and stored key. | M |

### 3.2 Document management (DOC)
| ID | Requirement | Pri |
|---|---|---|
| DOC-1 | Users can upload a PDF (text-based), .docx, .txt or .md file. | M |
| DOC-2 | Unsupported or invalid files are rejected with a clear error (type checked by content, not only by extension). | M |
| DOC-3 | Per-user limits are enforced: demo users max 3 documents, 5 MB per file; BYOK users max 100 documents, 25 MB per file. A single document may produce at most 2,000 chunks (`Documents:Ingestion:MaxChunksPerDocument`). The total number of chunks across all of a user's documents is also capped: BYOK users 20,000, demo users 3,000 (`Documents:Limits:*:MaxTotalChunks`; demo is 15% of BYOK, enough for one maximal document or several ordinary ones, and bounds the shared free-tier storage). The total is checked during ingestion before embedding, race-safe per user; a document that would exceed it is marked `Failed` with "Storage limit reached: N of LIMIT chunks used". Deleting documents frees capacity; usage is reported as `chunks: {used, limit}` by `GET /api/v1/me`. Values are configuration. If a user removes their key while above the demo limits, existing documents stay but new uploads are blocked until they are under the limit. | M |
| DOC-4 | Processing (extract → chunk → embed → store) runs in the background; the upload call returns immediately with a document ID and status. | M |
| DOC-5 | Users can see each document's status: `Pending`, `Processing`, `Ready`, `Failed` (with reason). | M |
| DOC-6 | Users can list their documents (name, type, size, page count, chunk count, status, upload date). | M |
| DOC-7 | Users can delete a document; its chunks are deleted too. | M |
| DOC-8 | A PDF with no extractable text is marked `Failed` with a "scanned PDFs are not supported" reason. | M |
| DOC-9 | Original files are discarded after text extraction. | M |
| DOC-10 | Each chunk keeps source metadata: document ID, file name, page number (PDF) or section/heading (docx/md), chunk position. | M |
| DOC-11 | When Arabic text in a PDF had to be reconstructed from reversed order, the document's details include an extraction notice (`extractionNotice`) explaining that Arabic text may be inaccurate (D31). | M |

### 3.3 Question answering and agent (QA)
| ID | Requirement | Pri |
|---|---|---|
| QA-1 | Users can ask a question against all their `Ready` documents or a chosen subset. | M |
| QA-2 | Retrieval only ever returns chunks owned by the requesting user. | M |
| QA-3 | **Agentic search:** the agent decides when and what to search, and can search again with a reformulated query if the results are weak. | M |
| QA-4 | **Query decomposition:** complex questions are split into sub-questions, each answered with retrieval, then combined. | M |
| QA-5 | **Answer verification:** each claim in the final answer is checked against retrieved chunks; unsupported claims are removed or flagged. | M |
| QA-6 | **Summarize:** the user can request a summary of a whole document. | M |
| QA-7 | **Compare:** the user can request a comparison of two documents. | M |
| QA-8 | Every answer returns citations: document ID, file name, page/section, and the quoted source text. | M |
| QA-9 | If the documents do not contain the answer, the response says so instead of inventing one. | M |
| QA-10 | Answers can be streamed to the client (Server-Sent Events) as well as returned whole. | S |
| QA-11 | The agent loop is capped per question: demo 4 tool calls and 60 s; BYOK 10 tool calls and 120 s. Values are configuration. On reaching a cap, the agent answers with what it has and says the answer may be incomplete. | M |
| QA-14 | Questions and documents in English and Arabic are supported; the answer is in the language of the question. | M |
| QA-12 | Each request is stateless: no conversation history is stored (D14). | M |
| QA-13 | The response states which mode served it (`demo` or `byok`) and which provider/model was used. | M |

### 3.4 LLM key management, BYOK (KEY)
| ID | Requirement | Pri |
|---|---|---|
| KEY-1 | A signed-in user can save one LLM key, choosing the provider (OpenRouter, Anthropic, OpenAI, Gemini) and a model. | M |
| KEY-2 | The key is validated with one low-cost call to the provider before it is saved; invalid keys are rejected with the provider's reason. | M |
| KEY-3 | Users can view their saved configuration (provider, model, last 4 characters of the key). The full key is never returned. | M |
| KEY-4 | Users can replace or delete their key. | M |
| KEY-5 | Users can list the models available for a provider with their key, filtered to models that support tool calling. | S |
| KEY-6 | When a user has a valid key, all their requests use it; otherwise demo mode is used. | M |
| KEY-7 | Provider errors from a user's key (invalid key, no credit, rate limited) are returned to the user as clear, specific errors. | M |
| KEY-8 | Only fixed, known provider endpoints are used; users cannot supply a custom base URL. | M |

### 3.5 Demo mode (DEMO)
| ID | Requirement | Pri |
|---|---|---|
| DEMO-1 | Signed-in users without a key are served by a free OpenRouter model using the operator's key. Anonymous visitors have no demo access. | M |
| DEMO-2 | Each demo user can ask at most 10 questions per day (summaries and comparisons count as questions). The value is configuration. | M |
| DEMO-3 | Demo mode has reduced limits (smaller agent tool-call cap, fewer documents) compared with BYOK. | M |
| DEMO-4 | If the primary demo model is unavailable or rate limited, a configured fallback free model is tried. | S |
| DEMO-5 | When demo limits are reached, the response explains this and suggests adding a key. | M |
| DEMO-6 | Demo model IDs are configuration values, not code. | M |

---

## 4. External Interface Requirements

### 4.1 API
- REST over HTTPS with JSON. File uploads use `multipart/form-data`.
- Versioned routes (`/api/v1/...`).
- OpenAPI document published at `/openapi/v1.json` in every environment (anonymous, read-only); any interactive UI is available in development only.
- Errors use RFC 9457 Problem Details.
- Endpoint list: see the development plan, §API surface.

### 4.2 External services
| Service | Use |
|---|---|
| Ollama (local) | Development chat model `gemma4:12b`. |
| OpenRouter | Demo chat model (operator key); BYOK provider (user key). |
| Anthropic, OpenAI, Google Gemini | BYOK providers (user key). |
| Google, GitHub OAuth | External sign-in. |
| Neon | Production PostgreSQL + pgvector. |
| SMTP email | Confirmation and password-reset emails (AUTH-7). Development: Mailpit (local container). Production: provider chosen in Phase 8. |

---

## 5. Non-Functional Requirements

### 5.1 Security (SEC)
| ID | Requirement |
|---|---|
| SEC-1 | User LLM keys are encrypted at rest with ASP.NET Core Data Protection; the key ring is stored separately from the database. |
| SEC-2 | Keys, passwords and tokens never appear in logs, traces, error messages or API responses. |
| SEC-3 | All data access is filtered by the authenticated user's ID (tenant isolation), enforced in the data layer, and covered by tests. |
| SEC-4 | HTTPS only in production; secure headers enabled. |
| SEC-5 | Uploaded file parsing is protected against oversized and malformed files (size limits, timeouts). |
| SEC-6 | Document text sent to the LLM is treated as untrusted (prompt-injection): document content cannot change tool permissions or reach another user's data. |
| SEC-7 | Secrets (OpenRouter demo key, OAuth secrets, DB connection string) come from environment/secret stores, never source control. |
| SEC-8 | Authentication endpoints are rate limited against brute force. |

### 5.2 Performance (PERF)
| ID | Requirement |
|---|---|
| PERF-1 | Upload request returns within 2 s (processing is in the background). |
| PERF-2 | Retrieval (embedding the query + vector search) completes within 1 s for a user with up to the maximum allowed chunks. |
| PERF-3 | Total answer time depends on the LLM provider and is not guaranteed; the agent timeout (OI-4) caps it. |

### 5.3 Reliability
- REL-1: A failed document job is marked `Failed` with a reason and can be retried by re-uploading.
- REL-2: Hosted provider calls get one retry on 5xx (including Anthropic 529) and network/connection errors, after a short backoff (Retry-After honoured, capped at 2 s). 429 and other 4xx are never retried, nor is a response that has begun streaming. In demo mode the primary model is retried once on 5xx/network errors and then the second (fallback) model is used; a 429 goes straight to the second model. The local Ollama client retries connection failures and 502/503/504 a configured number of times.
- REL-3: Background jobs that were interrupted by a restart are resumed or marked failed on startup.

### 5.4 Maintainability and portability
- MAIN-1: All LLM access goes through `IChatClient` / `IEmbeddingGenerator` (Microsoft.Extensions.AI).
- MAIN-2: Switching between dev (Ollama), demo (OpenRouter) and BYOK providers needs no code changes beyond configuration.
- MAIN-3: The API runs as a Docker container; the local environment starts with one command.
- MAIN-4: Automated tests: unit tests, plus integration tests against a real Postgres + pgvector (Testcontainers).

### 5.5 Observability
- OBS-1: Structured logging and OpenTelemetry traces for requests, ingestion jobs, retrieval and LLM calls (with token counts where available).
- OBS-2: Health check endpoints (liveness and readiness, including the database).

### 5.6 Privacy
- PRIV-1: Users are told that in demo mode their questions and document excerpts are sent to a free third-party model whose provider may log them.
- PRIV-2: Deleting a document or account permanently removes the related data.

---

## 6. Data Requirements (logical)

| Entity | Key fields |
|---|---|
| User | Identity user fields; external logins (Google, GitHub). |
| UserLlmCredential | UserId (1:1), Provider, Model, EncryptedKey, KeyLast4, ValidatedAt. |
| Document | Id, UserId, FileName, ContentType, SizeBytes, PageCount, Status, FailureReason, CreatedAt. |
| Chunk | Id, DocumentId, UserId, Ordinal, Text, PageNumber/Section, TokenCount, Embedding (vector). |
| DocumentText | Full extracted text per document, needed for summarize/compare (QA-6, QA-7); kept or derived from chunks (design detail in the plan). |
| IngestionJob | DocumentId, State, Attempts, LastError, timestamps. |

---

## 7. Model selection
| Role | Choice |
|---|---|
| Dev chat model | Ollama `gemma4:12b` (tool calling confirmed on this machine). |
| Demo chat model | A free OpenRouter model with tool-calling support; chosen in phase 6 and kept in configuration. |
| Embedding model | `multilingual-e5-small`, int8-quantized ONNX, in-process, 384 dimensions (supports English and Arabic). |

---

## 8. Acceptance Criteria (v1 API)
1. A user can register (email or Google/GitHub), upload each supported file type, see it reach `Ready`, and ask questions that return cited answers.
2. A user can never retrieve, cite, list or delete another user's documents (verified by integration tests).
3. A user with a valid key gets answers from their chosen provider; removing the key switches them to demo mode.
4. Demo mode enforces its limits and returns a clear message when they are exceeded.
5. Summarize and compare work on `Ready` documents and include citations.
6. The service runs on SnapDeploy (free tier) with Neon, within free tiers, using only configuration changes from the local setup.

---

## 9. Open Issues
All resolved on 2026-11-12.

| ID | Question | Resolution |
|---|---|---|
| OI-1 | Must users sign in to use the demo? | Yes, sign-in required (D17). |
| OI-2 | Demo question limit? | 10 per user per day (D18). |
| OI-3 | Document limits? | Demo 3 docs / 5 MB; BYOK 100 docs / 25 MB (D19). |
| OI-4 | Agent limits? | Demo 4 calls / 60 s; BYOK 10 calls / 120 s (D20). |
| OI-5 | Include Arabic in v1? | Yes; `multilingual-e5-small` int8 (D13). Arabic PDF extraction (right-to-left text) must be tested in Phase 2. |
| OI-6 | Auto-link external logins by email? | Yes, only for provider-verified emails (D21). |
| OI-7 | Email confirmation / reset in v1? | Yes (D22). Dev: Mailpit; production provider chosen in Phase 8. |

---

## 10. Frontend (Phase 9)

Status: **approved 2026-11-21 (with the Phase 9 plan); open issues OI-8 to OI-23 resolved 2026-11-21.** Sections 1 to 9 describe the API and stay valid; this section adds the web UI that consumes it (D2).

### 10.1 Scope
A web application ("the UI") for the DocAgent API. It covers every user-facing API capability of v1: sign-in and account recovery, documents, questions with streamed progress and citations, summary and compare, LLM key settings, the demo banner and privacy notice, the account page and storage usage.

Out of scope for the UI v1: admin or operator screens (the operator has none, §2.2), stored chat history (D14: the API is stateless), viewing or downloading original files (D15: not kept), two-factor authentication and the Identity `manage` endpoints (not used by v1), push notifications, offline use, a native mobile app.

### 10.2 Decisions recorded from the stakeholder (Phase 9)
| # | Decision |
|---|---|
| D33 | Frontend framework: Next.js (App Router) with React and TypeScript; styling with Tailwind CSS and shadcn/ui components. |
| D34 | Hosting: Vercel Hobby (free) (C1, D16). The Hobby plan is for non-commercial use; the operator accepts that restriction or re-decides before any commercial use. |
| D35 | Authentication is a **backend-for-frontend (BFF)**: the Next.js server logs in to the API, keeps the API access and refresh tokens in an encrypted `httpOnly`, `Secure`, `SameSite` cookie, refreshes them, and forwards the browser's calls to the API through route handlers or server actions. Browser JavaScript never sees a token. |
| D36 | Google and GitHub sign-in use the API's external flow (D24): the API redirects to a Next.js callback route with a one-time code, and the Next.js server exchanges it for tokens (`POST /api/v1/auth/external/exchange`). The code is never handled by browser JavaScript. |
| D37 | Languages: English and Arabic, with full right-to-left (RTL) layout for Arabic. This extends D13 to the UI. |
| D38 | The UI holds no application data of its own: no database, no stored questions or answers. Anything shown comes from the API on request; any conversation view lives only in the browser tab (open issue OI-12). |
| D39 | The UI calls the API server to server, so the API's CORS setting stays empty in production (the BFF is not a browser). Exception: D40. |
| D40 | Uploads use an **upload ticket** (OI-8, plan item A3): the BFF asks the API for a short-lived, single-use, upload-scoped ticket; the browser uploads the file directly to the API's upload endpoint with that ticket; CORS is allowed only on that endpoint and only for the UI origin. The browser never holds the access or refresh token. Full DOC-3 limits apply (5 MB demo, 25 MB BYOK). No interim 4 MB cap is needed, except that if sub-phase 9.2 is built before A3, it uses a 4 MB cap with a clear message until A3 lands. Decided 2026-11-21. |
| D41 | Client IP for rate limiting (OI-10, plan item A4): the BFF sends the visitor's IP in a trusted `X-Client-IP` header together with a shared secret (`Bff:SharedSecret`, header `X-Bff-Secret`); the API trusts the header only when the secret matches and uses it for the per-IP auth limit. Decided 2026-11-21. |
| D42 | Repository and branding (OI-18, OI-21): the UI lives in the `web/` folder of this repository with its own CI job and path filters; the Vercel project root is `web`. Design: neutral theme, one accent colour, text wordmark "DocAgent", no logo. Decided 2026-11-21. |
| D43 | API hosting changed (amends D8): Azure requires a credit card, so the API deploys to the **SnapDeploy free tier** (no card) with Neon free Postgres, Brevo SMTP and the OpenRouter demo. Limits: see C1a. No pre-deploy hook exists, so migrations run on startup (`MigrateOnStartup`), and the memory budget is 512 MB (ONNX model and .NET heap must fit). Azure Container Apps remains a documented alternative (Bicep and workflow kept). The Vercel UI is unchanged. Decided 2026-11-21. |

### 10.3 Functional requirements
Priority: **M** = must have (v1), **S** = should have, **C** = could have. "API:" names the endpoint (see the development plan §3). Messages for API errors follow §10.6.

#### 10.3.1 Sign-in and accounts (UI-AUTH)
| ID | Requirement | Pri |
|---|---|---|
| UI-AUTH-1 | A registration page creates an account with email and password (API: `POST /auth/register`). The password rules (D25) are shown before submit and server rule violations are listed per rule next to the field. | M |
| UI-AUTH-2 | After registering, the user sees a "check your email" page with a "resend confirmation email" action (API: `resendConfirmationEmail`, subject to the auth rate limit D27). Until the email is confirmed, documents and Q&A are not usable and the UI says why and how to fix it. | M |
| UI-AUTH-3 | The email confirmation link in the email is served by the API (AUTH-7). The UI must work with that link as is (open issue OI-9 asks whether the link should instead open the UI). After confirming, signing in again, or refreshing the account state, unlocks the app. | M |
| UI-AUTH-4 | A sign-in page with email and password (API: `POST /auth/login`). Wrong credentials (and, if the API ever required it, an unconfirmed email) get one non-leaking message (no "this email exists" hint); a locked account (5 failures) gets its own message. As built, the API lets an unconfirmed account sign in (`RequireConfirmedEmail=false`), so the unconfirmed state is shown after sign-in (UI-AUTH-2), not at the login form. | M |
| UI-AUTH-5 | "Forgot password" asks for the email and always shows the same confirmation message, whether or not the account exists (API: `forgotPassword`). A "reset password" page takes email, the emailed reset code and the new password (API: `resetPassword`). | M |
| UI-AUTH-6 | "Continue with Google" and "Continue with GitHub" buttons start the external flow (D36). A rejected sign-in (`?error=email_not_verified`, `no_verified_email`, `external_login_failed`) shows a clear message with the way back to sign-in (AUTH-5). | M |
| UI-AUTH-7 | Sign out clears the session cookie and calls no token to the browser. Signing out on one tab signs out the others on their next request. | M |
| UI-AUTH-8 | The session is renewed transparently: the BFF refreshes an expired access token with the refresh token (D26) and retries the call once; concurrent calls share one refresh. If refresh fails, the user is sent to sign-in and returns to the page they were on. | M |
| UI-AUTH-9 | Pages that need a signed-in user redirect anonymous visitors to sign-in; signed-in users visiting sign-in or register are redirected into the app. | M |
| UI-AUTH-10 | The UI shows the signed-in email and the linked sign-in methods (`GET /me`: `externalLogins`). | S |

#### 10.3.2 Documents (UI-DOC)
| ID | Requirement | Pri |
|---|---|---|
| UI-DOC-1 | A documents page lists the user's documents: name, type, size, pages, chunks, status, upload date (API: `GET /documents`, DOC-6), newest first. | M |
| UI-DOC-2 | Upload by file picker and by drag and drop, for PDF, .docx, .txt, .md. Before sending, the UI checks extension and size against the user's tier (demo 5 MB; BYOK 25 MB; D19) and shows the limit; the API remains the authority and its refusal is shown too (413, 415, 409 at the document limit). | M |
| UI-DOC-3 | Upload shows progress per file and supports several files in sequence (the API allows 2 concurrent uploads per user). Failures are per file and do not stop the others. | M |
| UI-DOC-4 | Status is shown as a labelled badge (not colour alone): `Pending`, `Processing`, `Ready`, `Failed`. While any document is `Pending` or `Processing`, the list refreshes itself (API: `GET /documents`, every 2 to 5 s with back-off, paused while the tab is hidden) until all are final. | M |
| UI-DOC-5 | A `Failed` document shows its `failureReason` (e.g. scanned PDF not supported, storage limit reached) and a hint to re-upload (REL-1). | M |
| UI-DOC-6 | When a document has an `extractionNotice` (Arabic PDF repaired, D31/DOC-11), the notice is shown on the document row and again with answers that cite it. | M |
| UI-DOC-7 | Delete a document with a confirmation dialog stating that its text and embeddings are removed permanently (API: `DELETE /documents/{id}`, DOC-7, PRIV-2). | M |
| UI-DOC-8 | The page shows how many documents the user has against the tier limit (demo 3 / BYOK 100) and disables upload at the limit with an explanation (suggesting a key in demo mode). | M |
| UI-DOC-9 | Document names are displayed as text only (never as HTML) and may be Arabic, mixed-direction or very long (truncated with the full name available). | M |
| UI-DOC-10 | Per-document actions: ask about this document, summarize, and select for compare (links into §10.3.3 and §10.3.4). | S |

#### 10.3.3 Ask (UI-QA)
| ID | Requirement | Pri |
|---|---|---|
| UI-QA-1 | An ask page with a question box (at most 2,000 characters, with a counter) and a document scope: all `Ready` documents (default) or a chosen subset (at most 100). Documents that are not `Ready` are not selectable. | M |
| UI-QA-2 | Submitting uses the streaming endpoint (`POST /ask/stream`, QA-10). The UI shows progress from the SSE events `searching`, `tool_call`, `verifying` and then the answer; the stream is relayed by the BFF without buffering and never exposes the token. | M |
| UI-QA-3 | The answer renders with its citations: each citation shows file name, page or section, and the quoted text; inline markers like `[1]` link to the matching citation (QA-8). Answer text is rendered as safe Markdown (no raw HTML). | M |
| UI-QA-4 | When `notFound` is true the UI shows a distinct "not found in your documents" state instead of a normal answer (QA-9). | M |
| UI-QA-5 | When `incomplete` is true the UI shows a "may be incomplete (limit reached)" notice (QA-11). | M |
| UI-QA-6 | The UI shows which mode answered (`demo` or `byok`) and the provider and model (QA-13). `notices` are shown with the answer (privacy notice first in demo, PRIV-1; extraction notices). | M |
| UI-QA-7 | The user can cancel a running question; cancelling aborts the request to the API so work stops server-side. Only one question per tab runs at a time; the API allows 4 per user (429 is explained). | M |
| UI-QA-8 | An `error` SSE event, a dropped connection, and an error before the stream starts each show the message for their code (§10.6) and keep the question text so the user can retry. | M |
| UI-QA-9 | The question and answer are right-to-left or left-to-right by their own text direction (D37), independent of the interface language. The answer is in the question's language (QA-14); the UI does not translate it. | M |
| UI-QA-10 | Whether the UI shows tool-call details (tool name per `tool_call` event) or only generic stages is decided in OI-11. | S |
| UI-QA-11 | Conversation view: the current tab keeps the questions and answers asked so far as a list (not stored anywhere, lost on reload; each question remains independent, QA-12). | S |
| UI-QA-12 | Copy the answer (with citations) to the clipboard. | C |

#### 10.3.4 Summary and compare (UI-SUM)
| ID | Requirement | Pri |
|---|---|---|
| UI-SUM-1 | Summarize one `Ready` document with an optional focus (at most 500 characters) (API: `POST /documents/{id}/summary`, QA-6). The result uses the same answer view as §10.3.3 (citations, notices, mode, `incomplete`). | M |
| UI-SUM-2 | Compare two different `Ready` documents with an optional focus (API: `POST /documents/compare`, QA-7). The UI prevents choosing the same document twice. | M |
| UI-SUM-3 | Summary and compare are not streamed by the API; the UI shows an indeterminate "working" state with a cancel action and the expected wait (up to the agent timeout, D20). | M |
| UI-SUM-4 | In demo mode the UI states before the user submits that a summary or comparison costs one of the daily questions (DEMO-2). | M |

#### 10.3.5 LLM key settings (UI-KEY)
| ID | Requirement | Pri |
|---|---|---|
| UI-KEY-1 | A settings page shows the saved configuration: provider, model, and the last 4 characters of the key (API: `GET /me/llm-key`, KEY-3); when none is saved it explains demo mode and the benefits of a key (higher limits). The full key is never displayed. | M |
| UI-KEY-2 | A form to save or replace the key: provider (openrouter, anthropic, openai, gemini), key, model (API: `PUT /me/llm-key`, KEY-1, KEY-4). The API validates the key with a live call; the UI shows a progress state and the specific reason on rejection (KEY-2, KEY-7, §10.6). | M |
| UI-KEY-3 | The model is chosen from a list of tool-capable models loaded with the entered key (API: `GET /providers/{provider}/models`, key sent in the `X-Llm-Api-Key` header by the BFF; KEY-5). If listing fails, or the user prefers, the model can be typed in (the API validates the pattern, at most 200 characters). | S |
| UI-KEY-4 | Delete the key with a confirmation that states the consequences: demo model and demo limits apply; documents above the demo limits stay but new uploads are blocked until under the limit (DOC-3). | M |
| UI-KEY-5 | The key field is a password-type input with autocomplete off, never prefilled, cleared after submit, and not kept in browser storage, the URL, analytics or logs (UI-SEC-6). A short note says where the key is stored (encrypted at the API, SEC-1). | M |
| UI-KEY-6 | The key form respects the API limit of 10 key actions per minute: a 429 shows the wait time. | M |

#### 10.3.6 Demo mode and notices (UI-DEMO)
| ID | Requirement | Pri |
|---|---|---|
| UI-DEMO-1 | While `GET /me` returns `demo`, every signed-in page shows a demo banner: "Demo mode: N of 10 questions left today, resets at (local time of `resetsAt`)", with a link to the key settings (DEMO-1, DEMO-5). The numbers come from the API (`limit`, `used`, `remaining`), not hard-coded. | M |
| UI-DEMO-2 | The banner updates after every question, summary or comparison (re-read `GET /me`), and when `remaining` is 0 the ask and summary/compare controls are disabled with the reset time and a link to add a key. The API still enforces the limit (`demo_quota_exceeded`). | M |
| UI-DEMO-3 | The privacy notice from the API (PRIV-1, `demo.privacyNotice`) is shown in the banner or a dialog the first time in a session and is available on demand, and shown with every demo answer. Its text comes from the API and is displayed in the API's language (it is not translated by the UI; see OI-13). | M |
| UI-DEMO-4 | The banner is absent for BYOK users (no `demo` object), and a separate hint is shown when demo is unavailable on the server (`demo_unavailable`). | M |

#### 10.3.7 Account and storage (UI-ACC)
| ID | Requirement | Pri |
|---|---|---|
| UI-ACC-1 | An account page shows email, confirmation state, linked sign-in methods, and the mode (demo or own key). | M |
| UI-ACC-2 | Storage usage is shown as `chunks.used` of `chunks.limit` (a progress meter with text), with a hint when above 80% and an explanation of what fills it and how to free space (delete documents) (DOC-3). | M |
| UI-ACC-3 | Delete account: a confirmation dialog listing what is removed (documents, chunks, stored key, demo usage), requiring the user to type their email or a confirmation word, then (API: `DELETE /me`, AUTH-8) clears the session and shows a goodbye page. | M |
| UI-ACC-4 | The usage meter is also shown on the documents page. | S |

#### 10.3.8 Application shell and general behavior (UI-GEN)
| ID | Requirement | Pri |
|---|---|---|
| UI-GEN-1 | A responsive app shell with navigation (Documents, Ask, Settings, Account), language switcher, sign-out, and the demo banner (UI-DEMO-1). | M |
| UI-GEN-2 | Every data view has designed loading, empty, error-with-retry and success states. Empty documents page explains the file types and limits. | M |
| UI-GEN-3 | A public landing page names the product, says what it does in one paragraph, states that sign-in is required (D17) and links to sign-in/register and the privacy notice. | M |
| UI-GEN-4 | A privacy page states what is sent where: demo questions and excerpts go to a free third-party model that may log them (PRIV-1); BYOK data goes to the user's chosen provider; originals are not kept (D15); cookies used (session and language only). | M |
| UI-GEN-5 | A dark theme following the operating system setting, with a manual toggle (open issue OI-15). | S |
| UI-GEN-6 | Custom 404 and error pages; no stack traces or API internals shown to users. | M |
| UI-GEN-7 | A health page or footer link showing API availability is not required; the UI instead shows a friendly "service is starting, try again" message when the API is cold-starting (sleep after idle, up to about 60 s on SnapDeploy) with automatic retry. | M |

### 10.4 Non-functional requirements

#### 10.4.1 Security (UI-SEC)
| ID | Requirement |
|---|---|
| UI-SEC-1 | API access and refresh tokens exist only on the Next.js server and in an encrypted, authenticated session cookie (AES-256-GCM or equivalent, secret from environment). Tokens are never in HTML, props, JSON responses to the browser, URLs, `localStorage`, `sessionStorage` or any non-`httpOnly` cookie (D35). A test proves no token substring reaches a browser-visible response. |
| UI-SEC-2 | The session cookie is `HttpOnly`, `Secure` (HTTPS only; the `__Host-` prefix is used in production), `SameSite=Lax` (or `Strict` if the external callback flow still works), `Path=/`, without a `Domain` attribute, with an absolute lifetime not above the refresh token's 14 days (D26) and renewed on use. Cookie size stays under 4 KB or the session is split across chunked cookies. |
| UI-SEC-3 | CSRF: every state-changing BFF route (POST, PUT, PATCH, DELETE, server actions) verifies the `Origin` (or `Sec-Fetch-Site`) header against the site's own origin and rejects mismatches; the session cookie is `SameSite`; no state-changing action uses GET (sign-out included). The external callback is protected against login CSRF with a one-time `state` nonce that the BFF puts in a short-lived `httpOnly` cookie and in the `returnUrl` query string, and checks before exchanging the code. |
| UI-SEC-4 | A Content Security Policy is sent on every page, built with a per-request nonce, with at least: `default-src 'self'`; `script-src 'self' 'nonce-…' 'strict-dynamic'` (no `unsafe-eval` in production); `style-src 'self'` (and nonce or hashes as required); `img-src 'self' data:`; `connect-src 'self'` (the browser talks only to the UI origin, never to the API or any third party); `object-src 'none'`; `base-uri 'none'`; `form-action 'self'`; `frame-ancestors 'none'`. Further headers: `Strict-Transport-Security`, `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer` (the callback URL carries a code), `Permissions-Policy` with everything off, `Cross-Origin-Opener-Policy: same-origin`. |
| UI-SEC-5 | The external-login callback route removes the code from the URL immediately (server-side redirect after exchange, `Cache-Control: no-store`, `Referrer-Policy: no-referrer`) so it cannot leak through history, logs or referrers; a code that fails or was used shows the sign-in page with an error. |
| UI-SEC-6 | Secrets and user content are never logged or sent to analytics: LLM API keys, passwords, tokens, reset codes, question text, answer text, document names and text. The BFF logs method, route, status and a request ID only. Error reporting, if any, strips request bodies and headers. |
| UI-SEC-7 | The BFF forwards only an allowlist of API routes and methods (the endpoints in §3 of the plan), builds the target URL itself (no user-supplied URL or host, so no SSRF), and does not forward cookies or unknown headers. Request size limits are enforced before forwarding. |
| UI-SEC-8 | User-provided and model-provided text (file names, answers, quotes, notices, error details) is rendered as text or as sanitized Markdown; no `dangerouslySetInnerHTML` on untrusted data; links in answers open with `rel="noopener noreferrer"` and only `http(s)` and relative links are allowed. |
| UI-SEC-9 | Environment secrets (session encryption key, API base URL) live only in Vercel project settings and a local git-ignored `.env.local`; `NEXT_PUBLIC_*` variables hold no secrets; the repository contains only `.env.example` with placeholders. A CI check fails the build if a secret pattern is committed. |
| UI-SEC-10 | Open redirects are prevented: any post-sign-in `next` target must be a relative path on the same site. |
| UI-SEC-11 | Dependencies are pinned by lockfile, audited in CI (`npm audit --omit=dev` at high severity fails the build), and updated through reviewed pull requests; `npm ci` is used in CI. |
| UI-SEC-12 | The browser's requests to the UI are rate limited per IP at the edge by Vercel's defaults; the BFF additionally passes the real client IP to the API (open issue OI-10) so the API's per-IP auth limit (D27) applies per visitor and not to all users at once. |

#### 10.4.2 Accessibility (UI-A11Y)
Target: WCAG 2.2 level AA for all pages in §10.3.
| ID | Requirement |
|---|---|
| UI-A11Y-1 | Everything works with the keyboard alone, in a logical order, with a visible focus indicator (focus not obscured, WCAG 2.4.11) and a "skip to content" link. No keyboard traps; dialogs trap focus while open and return it on close. |
| UI-A11Y-2 | Text contrast at least 4.5:1 (3:1 for large text and UI components) in both light and dark themes, checked in automated tests for the design tokens. Information is never conveyed by colour alone (status badges carry text). |
| UI-A11Y-3 | Every form control has a visible label; errors are tied to fields (`aria-describedby`, `aria-invalid`), summarised at the top of a failed form with focus moved to the summary, and phrased with how to fix them. Redundant entry is avoided (WCAG 3.3.7) and passwords can be pasted and shown (3.3.8). |
| UI-A11Y-4 | Dynamic changes are announced: document status changes, streamed progress stages, answer ready, and errors use polite live regions (assertive only for blocking errors). Progress is not announced for every poll, only on change. |
| UI-A11Y-5 | Targets are at least 24 by 24 CSS pixels (WCAG 2.5.8); the layout reflows at 320 CSS pixels wide without horizontal scrolling and supports 200% zoom and text spacing overrides. |
| UI-A11Y-6 | Motion respects `prefers-reduced-motion`; nothing flashes. |
| UI-A11Y-7 | Pages have unique titles, one `h1`, landmarks (`header`, `nav`, `main`), the correct `lang` and `dir` on `html` (and on inline text in another language or direction). |
| UI-A11Y-8 | Automated checks (axe via Playwright) run on every page in both languages, both directions and both themes in CI, with zero serious or critical violations; a manual keyboard and screen-reader pass (NVDA or VoiceOver) is done before release for sign-in, upload, ask and key settings. |

#### 10.4.3 Internationalization (UI-I18N)
| ID | Requirement |
|---|---|
| UI-I18N-1 | The interface is available in English (`en`, default) and Arabic (`ar`). All strings live in message catalogs; no user-visible text is hard-coded in components. A CI check fails when a key is missing in one language. |
| UI-I18N-2 | Language is chosen by, in order: the user's explicit choice (stored in a `NEXT_LOCALE`-style cookie containing no personal data), then the `Accept-Language` header, then English. The language is in the URL (`/en/...`, `/ar/...`) so pages are linkable. The switcher is on every page, including before sign-in. |
| UI-I18N-3 | Arabic uses full RTL: `dir="rtl"` on `html`; layout built with CSS logical properties (`ms-*`, `me-*`, `start`, `end`) rather than left/right; icons with direction (arrows, chevrons, progress) are mirrored, logos and media controls are not; shadcn/ui components are verified in RTL (menus, dialogs, tooltips, tables, toasts, sliders). |
| UI-I18N-4 | Fonts: an Arabic-capable family with matching weights (for example Noto Sans Arabic or IBM Plex Sans Arabic) alongside the Latin family, self-hosted through `next/font` (no third-party font requests, UI-SEC-4), with sensible line height for Arabic. |
| UI-I18N-5 | Numbers, dates, times, file sizes and plurals use `Intl` APIs with the active locale (Arabic plural forms, see ICU). Arabic uses Western digits (0 to 9) by default, which is the numbering the API and documents use; whether to use Arabic-Indic digits is in OI-14. Dates and times are shown in the user's local time zone; `resetsAt` and `createdAt` come from the API in UTC. |
| UI-I18N-6 | Content from users and models keeps its own direction: `dir="auto"` (or `<bdi>`) on file names, questions, answers and quotes so mixed Arabic and English text and punctuation display correctly. Text boxes use `dir="auto"`. |
| UI-I18N-7 | API error text that the UI controls is translated by `code` (§10.6). Text that comes from the API (Problem Details `detail`, notices, the privacy notice) is English only and is shown as given, in a `dir="ltr"` block, or replaced by a translation where the code is known. |
| UI-I18N-8 | Translations are reviewed by an Arabic speaker before release (the stakeholder or a named reviewer); machine translation may be used for first drafts only. |

#### 10.4.4 Performance (UI-PERF)
| ID | Requirement |
|---|---|
| UI-PERF-1 | Core Web Vitals targets on a mid-range phone over fast 4G for the landing, sign-in and documents pages: LCP at most 2.5 s, INP at most 200 ms, CLS at most 0.1, measured with Lighthouse CI in the pipeline (budget, not a guarantee, since the API may be cold). |
| UI-PERF-2 | First-load JavaScript for the landing and sign-in pages at most 150 KB gzipped; heavy components (Markdown renderer, upload) are code-split; server components are the default. |
| UI-PERF-3 | The BFF adds at most 100 ms median to an API call, excluding the API's own time, and relays the SSE stream with no buffering (first event reaches the browser as soon as the API sends it). |
| UI-PERF-4 | Streaming and long calls fit Vercel's function duration limit: the BFF routes for ask, summary and compare set a maximum duration of at least the BYOK agent timeout plus margin (D20: 120 s, so 150 s or more). The value allowed on Hobby is verified in Phase 9.0 (open risk). |
| UI-PERF-5 | Polling is bounded (UI-DOC-4): no more than one request per 2 s, stopped when the tab is hidden or all documents are final. |

#### 10.4.5 Browser, device and other
| ID | Requirement |
|---|---|
| UI-BRW-1 | Supported browsers: the last two major versions of Chrome, Edge, Firefox and Safari (desktop and mobile). No Internet Explorer. JavaScript is required (the sign-in and settings forms are server actions and progressively enhanced where practical). |
| UI-BRW-2 | Responsive from 320 px wide up. Mobile support level is in OI-16 (recommended: fully usable on a phone, upload and ask included). |
| UI-OPS-1 | Free to run (D16, C1): Vercel Hobby, no paid add-ons, no paid third-party services (no paid analytics, error tracking or fonts). |
| UI-OPS-2 | No analytics or tracking by default; if enabled (OI-17), only cookie-less, privacy-preserving counters without personal data, and the privacy page is updated. |
| UI-OPS-3 | The UI repository folder builds and tests with one command each (`npm ci`, `npm run build`, `npm test`); CI runs lint, type check, unit tests, e2e tests, accessibility checks and the build on every pull request. |
| UI-OPS-4 | Every deploy is reproducible from git: preview deployments for pull requests, production from `main`; environment variables are documented in `.env.example`. |

### 10.5 External interfaces of the UI
| Interface | Detail |
|---|---|
| Browser to UI | HTTPS to the Vercel domain. Pages are server-rendered React; interactive parts call same-origin BFF route handlers or server actions. No third-party origins (UI-SEC-4). |
| UI (BFF) to API | Server-to-server HTTPS to the API base URL (environment variable `DOCAGENT_API_URL`), with `Authorization: Bearer <access token>`. The API is not called from the browser, so the API's CORS stays off (D39). |
| API to UI (external login) | The API redirects the browser to `https://<ui-host>/<locale-independent callback path>?s=<nonce>&code=<code>` (or `error=`). The URL must be on the API's `Authentication:External:AllowedReturnUrls` (D24). The validator matches scheme, host, port and path prefix and allows a query string, so the nonce can ride in `returnUrl`. |
| Email links | Confirmation emails link to the API (`GET /auth/confirmEmail?userId=&code=`); password reset emails carry a code the user types into the UI (`resetPassword`). See OI-9. |

#### Routes used (from the API as built)
| UI feature | API call (all under `/api/v1`) | Notes |
|---|---|---|
| Register | `POST /auth/register` `{email, password}` | Identity validation problems (400) list password rule failures. |
| Sign in | `POST /auth/login` `{email, password}` | 200 returns `{tokenType, accessToken, expiresIn, refreshToken}`; 401 for bad credentials or unconfirmed email. |
| Refresh | `POST /auth/refresh` `{refreshToken}` | New pair; the BFF replaces both and serializes concurrent refreshes. |
| Resend confirmation | `POST /auth/resendConfirmationEmail` `{email}` | Always succeeds (D29). |
| Forgot / reset | `POST /auth/forgotPassword` `{email}`, `POST /auth/resetPassword` `{email, resetCode, newPassword}` | The code is typed from the email. |
| External start | `GET /auth/external/{google\|github}?returnUrl=` | Browser navigation (redirect), initiated by the BFF's own route. |
| External exchange | `POST /auth/external/exchange` `{code}` | Same response as login; 400 for an invalid, expired or used code. |
| Account | `GET /me`, `DELETE /me` | `GET /me`: `id, email, emailConfirmed, externalLogins[], hasLlmKey, demo?{limit, used, remaining, resetsAt, privacyNotice}, chunks{used, limit}`. |
| Documents | `GET /documents`, `GET /documents/{id}`, `POST /documents` (multipart, field `file`; 202 `{id, fileName, status}`), `DELETE /documents/{id}` | Document: `id, fileName, contentType, sizeBytes, pageCount, chunkCount, status, failureReason, extractionNotice, createdAt`. |
| Ask | `POST /ask` and `POST /ask/stream` `{question, documentIds?}` | Stream events: `searching`, `tool_call {tool}`, `verifying`, `answer {…}`, `error {status, title, code?, retryAfter?}`. Answer: `answer, notFound, citations[{index, documentId, fileName, pageNumber, section, quote}], mode, provider, model, incomplete, toolCalls, notices[]`. |
| Summary / compare | `POST /documents/{id}/summary` `{focus?}`, `POST /documents/compare` `{documentIdA, documentIdB, focus?}` | Same answer shape, not streamed. |
| Key | `GET/PUT/DELETE /me/llm-key`, `GET /providers/{provider}/models` (header `X-Llm-Api-Key`) | `PUT {provider, model, apiKey}`; `GET` returns `{provider, model, last4, updatedAt}` or 404. |

### 10.6 Error handling: API errors to user messages
The BFF passes the API's Problem Details (`status`, `title`, `detail`, `code`, `retryAfter`) to the UI in a fixed shape and never includes tokens or request bodies. The UI shows a message chosen by `code` first, then by status. Texts below are the English wording to be translated (UI-I18N-7); each message has a next step.
| Source | Condition | Message (English) | Action |
|---|---|---|---|
| `code: demo_quota_exceeded` (429) | Daily demo limit used | "You have used today's demo questions. They reset at {time}. Add your own API key to keep going now." | Link to key settings; disable ask controls until `resetsAt` (UI-DEMO-2). |
| `demo_rate_limited` (429, `Retry-After`) | Demo models busy | "The free demo models are busy. Try again in {n} seconds." | Retry button with countdown. |
| `demo_unavailable` (503) | Demo down or misconfigured | "The demo is unavailable right now. You can add your own API key to use DocAgent." | Link to key settings. |
| `llm_key_required` (403) | No key and demo off | "Add an API key in Settings to ask questions." | Link. |
| `llm_key_invalid` (400 on save, 422 in use) | Provider rejected the key | On save: "The provider did not accept this key. Check that you copied it fully and that it is for {provider}." In use: "Your saved key was rejected by the provider. Replace it in Settings." | Focus key field / link. |
| `llm_no_credit` (402) | No provider credit | "Your {provider} account has no credit left. Add credit with the provider or use another key." | Link. |
| `llm_rate_limited` (429) | Provider rate limit | "Your provider is limiting requests. Try again in {n} seconds." | Retry. |
| `llm_unavailable` (503) | Provider down | "The model provider is not responding. Try again in a minute." | Retry. |
| `llm_model_unknown` (400/422) | Model not found | "That model is not available for this key. Choose another model." | Focus model field. |
| `llm_request_rejected` (502) | Provider refused the request | "The provider could not process this request. Try again, or choose another model." | Retry. |
| `llm_key_unreadable` (409) | Stored key cannot be decrypted | "We could not read your saved key. Please save it again." | Link. |
| 400 (no code) | Validation (question length, focus, same document twice, malformed form) | The field-level message from `detail` mapped to a translated message where known; otherwise a generic "Check the highlighted fields". | Field focus. |
| 400 from Identity (register, reset) | Password rules, duplicate email, invalid reset code | Per-rule messages next to the field (UI-AUTH-1). A duplicate email says "Could not create the account with these details", plus a link to sign in. (The API itself does report `DuplicateUserName`/`DuplicateEmail`, verified at 9.1; the UI does not repeat it.) | Field focus. |
| 401 | No or expired session; wrong credentials | Mid-session: silent refresh, else "Your session ended. Sign in again." Login: "Email or password is incorrect, or the email is not confirmed yet." | Sign-in link; "Resend confirmation". |
| 403 (no code) | Email not confirmed | "Confirm your email address first. We sent you a link." | Resend. |
| 404 | Document gone or not yours | "This document no longer exists." | Refresh the list. |
| 409 | Document limit reached; document not Ready; key conflict | Specific by endpoint: "You have reached your document limit ({n})."; "This document is not ready yet."; "Another change happened at the same time, try again." | Per endpoint. |
| 413 | File too large | "This file is larger than your limit of {size}." | None. |
| 415 | Unsupported type | "Only PDF, Word (.docx), text (.txt) and Markdown (.md) files are supported." | None. |
| 429 (no code) | Auth, key, upload or concurrency limit | "Too many requests. Try again in {n} seconds." (`Retry-After`), or "You already have the maximum number of questions running." | Retry. |
| 500 | Server fault | "Something went wrong on our side. Try again." | Retry; show request ID. |
| 502 / 503 / 504 (no code) | API or model unavailable, timed out | "The service is not responding" / "The question took too long. Try a narrower question or fewer documents." | Retry. |
| SSE `error` event | Error during a stream | Same table, using the event's `status` and `code`. | Keep the question text. |
| Network failure / BFF cannot reach the API | Offline, cold start | "Cannot reach the service. Check your connection; the service may be waking up." | Automatic retry with back-off for idempotent reads. |
| Extraction `failureReason` | Document `Failed` | The API's reason, shown as given, plus the translated hint (scanned PDF, storage limit). | Re-upload / delete. |

### 10.7 Acceptance criteria (Phase 9)
1. A new user can register, confirm the email, sign in, upload each supported file type, see it reach `Ready`, ask a question with streamed progress and read a cited answer, in English and in Arabic, on a desktop and a phone.
2. The user can also sign in with Google and with GitHub, including the case of an unverified provider email (clear rejection).
3. Browser inspection (network tab, storage, page source, JavaScript) shows no API token anywhere; the session cookie has the flags in UI-SEC-2; a cross-site POST to a BFF route is rejected (UI-SEC-3); the CSP in UI-SEC-4 is sent without violations.
4. A demo user sees the banner with the live allowance and privacy notice, is stopped with a clear message at 0 remaining, and sees it disappear after saving a valid key; deleting the key returns the banner.
5. Summary and compare work with citations; key save, replace, delete and model listing work, and the last 4 characters are the only part of the key ever shown.
6. Every error code in §10.6 has a tested message in both languages.
7. Arabic pages are fully mirrored (no left/right artefacts), mixed Arabic and English content displays correctly, and the Arabic font renders without missing glyphs.
8. Automated: unit tests, e2e tests, and axe checks pass in CI; Lighthouse budgets in UI-PERF-1/2 pass on the deployed site; a manual keyboard and screen-reader pass is recorded.
9. The deployed site on Vercel talks to the deployed API (SnapDeploy + Neon) within free tiers, with only configuration differences from local.
10. Deleting the account from the UI removes all data and the session, and signing in again starts from an empty account.

### 10.8 Constraints and risks that shape the requirements
- Vercel serverless functions limit a request body to about 4.5 MB, which is below the BYOK file limit (25 MB, D19) and the demo limit (5 MB). Uploads proxied through a Vercel function cannot meet DOC-3 as written. Options are in OI-8. To be re-verified against Vercel's current documentation in Phase 9.0.
- All BFF calls reach the API from Vercel's shared IPs. The API's per-IP auth limit (D27, 10 per minute) would throttle all users together unless the client IP is forwarded safely (OI-10).
- The email confirmation link points to the API, not the UI (OI-9).
- Vercel Hobby limits (function duration, invocations, bandwidth) and its non-commercial-use clause (D34).
- The session cookie must hold two tokens; the encrypted size may exceed 4 KB (UI-SEC-2: chunked cookies as the fallback).
- SnapDeploy sleeps the API after 15 minutes idle and needs about 1 minute to wake, so the first request after idle can take up to about 60 s (UI-GEN-7; previously about 30 s on Azure).
- SnapDeploy limits each IP to 100 requests per minute at its edge (429). All BFF calls come from Vercel's IPs, so the BFF must keep its calls lean (no polling bursts, no per-render fan-out) and the 9.7 smoke test checks it. The container-hour budget (100 per month account-wide) also limits always-on use.

### 10.9 Open Issues (Phase 9)
All resolved on 2026-11-21. Where the stakeholder named a decision it is recorded as D40 to D43; for the rest the recommended default was accepted as written (shown as the resolution). "Affects" says which sub-phase the decision applies to.

| ID | Question | Resolution (2026-11-21) | Affects |
|---|---|---|---|
|  OI-8 | **Large uploads.** Vercel functions accept bodies of about 4.5 MB, but the API allows 5 MB (demo) and 25 MB (BYOK). How should uploads work? (a) Cap UI uploads at 4 MB for everyone and document the difference; (b) an API change: a short-lived, single-use, upload-scoped ticket that the BFF obtains and the browser uses to send the file directly to the API (needs CORS on that one route and means the browser holds a limited-purpose ticket, never the access or refresh token); (c) host the UI where the body limit is higher (not free). | **Resolved: upload ticket (D40, plan A3).** Option (b); CORS only on the upload endpoint for the UI origin; full DOC-3 limits; 4 MB cap only if 9.2 lands before A3. | 9.2  |
|  OI-9 | **Email links.** The confirmation link opens an API page (plain response), not the UI. Should the API generate links to the UI (a configurable base URL, for example `https://<ui>/confirm?userId=&code=`), with the UI calling `confirmEmail` server-side? Password reset already uses a typed code; should it become a link too? | **Resolved: recommended default accepted.** Yes for confirmation (small API change: `Email:ConfirmationUrlBase`, UI page `/confirm`); keep the typed reset code in v1 (works as built). | 9.1  |
|  OI-10 | **Client IP for rate limiting.** All users reach the API from Vercel's IPs, so the per-IP auth limit would apply to everyone together. How to fix: (a) BFF sends the client IP in a header together with a shared secret and the API trusts it only with the secret; (b) raise the auth limit and rely on per-account limits; (c) leave as is for the demo. | **Resolved: trusted `X-Client-IP` header plus shared secret (D41, plan A4).** Option (a). | 9.1  |
|  OI-11 | Does the UI show tool-call details (for example "searching documents", tool names) or only generic stages? | **Resolved: recommended default accepted.** Generic translated stages (Searching, Checking sources, Verifying) from the event names; tool names are not shown (they are internal and English). | 9.3  |
|  OI-12 | Chat history. The API is stateless (D14). Keep a running conversation only in the open browser tab (UI-QA-11), keep nothing, or store history in the browser (`localStorage`/IndexedDB)? | **Resolved: recommended default accepted.** Tab-only list, lost on reload; nothing stored (also a privacy point). | 9.3  |
|  OI-13 | The API's `privacyNotice`, `notices` and Problem Details `detail` are English only. Is it acceptable that these show in English in the Arabic UI, or should the API add Arabic text (for example via `Accept-Language`)? | **Resolved: recommended default accepted.** The UI translates by `code` where a code exists and shows the privacy notice from a UI-side Arabic translation of the same text (kept in sync by a test comparing the English text); other API text is shown as given. | 9.5  |
|  OI-14 | Arabic numerals: Western digits (0-9) or Arabic-Indic digits in the Arabic UI? | **Resolved: recommended default accepted.** Western digits. | 9.6  |
|  OI-15 | Dark mode: required in v1? | **Resolved: recommended default accepted.** Yes (should have): follows the system setting with a manual toggle. | 9.6  |
|  OI-16 | Mobile support level: desktop-first, fully responsive, or mobile-first? | **Resolved: recommended default accepted.** Fully responsive from 320 px; all features work on a phone; no native app. | 9.0  |
|  OI-17 | Analytics: none, or privacy-friendly counters (Vercel Web Analytics)? | **Resolved: recommended default accepted.** None in v1 (UI-OPS-2). | 9.7  |
|  OI-18 | Repository layout: the `web/` folder in this repository (as the plan's solution layout says), or a separate repository? | **Resolved: `web/` folder in this repository, own CI job, Vercel root `web` (D42).** | 9.0  |
|  OI-19 | Vercel account: which Vercel account or team owns the project, and who connects it to the GitHub repository? Hobby plans cannot use a team with colleagues. | **Resolved: recommended default accepted.** The stakeholder's personal Hobby account, repository connected with the Vercel GitHub app limited to this repository. | 9.7  |
|  OI-20 | Custom domain: use the free `*.vercel.app` name, or a custom domain? (Needed for the Google/GitHub OAuth redirect and the API's `AllowedReturnUrls`.) | **Resolved: recommended default accepted.** `*.vercel.app` for v1 (free); a custom domain later changes only configuration. | 9.7  |
|  OI-21 | Design direction: brand name styling, logo, colours, tone. The product name is DocAgent (D23). | **Resolved: neutral theme, one accent colour, text wordmark "DocAgent", no logo (D42).** | 9.0  |
|  OI-22 | Who reviews the Arabic translation (UI-I18N-8) and by when? | **Resolved: recommended default accepted.** The stakeholder reviews the Arabic strings at the 9.1 checkpoint (auth pages) and before 9.7. | 9.1  |
|  OI-23 | Pre-production environments: should a Vercel preview deployment (every pull request) be allowed to talk to the real production API, or only to a local/staging API? Previews have different URLs that are not on the OAuth allowlist. | **Resolved: recommended default accepted.** Previews use no external login and point to a local or mocked API only; production deployment uses the production API. | 9.7  |
