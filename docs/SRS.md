# Software Requirements Specification (SRS)
## Document Q&A API with RAG and Agent Workflows

| Field | Value |
|---|---|
| Version | 0.2 (draft, awaiting approval) |
| Date | 2026-11-12 |
| Status | All open issues resolved (§9); awaiting stakeholder approval |

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
| D8 | Hosting: API on Azure Container Apps (free monthly grant); database on Neon (free tier, Postgres + pgvector). |
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
| D19 | Document limits: demo 3 documents of up to 5 MB each; BYOK 100 documents of up to 25 MB each (OI-3). |
| D20 | Agent limits: demo 4 tool calls / 60 s timeout; BYOK 10 tool calls / 120 s timeout (OI-4). |
| D21 | External logins link automatically to an existing account with the same email, only when the provider reports the email as verified (OI-6). |
| D22 | Email confirmation and password reset are in v1, sent through Gmail SMTP with an app password (OI-7). |
| D23 | Product/solution name: **DocAgent**. |

---

## 2. Overall Description

### 2.1 Product perspective
A new, standalone backend service. Clients (first API consumers and Swagger, later the Next.js UI) call it over HTTPS.

```
Client ──HTTPS──► ASP.NET Core API (Azure Container Apps)
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
- Production: Azure Container Apps + Neon PostgreSQL with the pgvector extension.
- Development: local machine (WSL2 Linux) with Docker and Ollama.

### 2.4 Constraints
- C1: Every hosted component must fit a free tier (Azure Container Apps free grant, Neon free tier).
- C2: Neon free tier storage is limited (0.5 GB at the time of writing), so per-user document limits are required (D19). Only extracted text and embeddings are stored (originals are discarded), which is much smaller than the file sizes, but many BYOK users at the 100-document limit could still fill it; storage usage must be monitored.
- C6: Gmail SMTP has a daily sending limit (about 500 messages/day for a personal account), which caps confirmation and reset emails.
- C3: The embedding model must be identical in every environment; changing it requires re-embedding all chunks.
- C4: Free OpenRouter models have per-minute and per-day request limits, and individual free models can be withdrawn.
- C5: The chat model must support tool calling.

### 2.5 Assumptions and dependencies
No assumptions beyond the decisions in §1.4 are made. Dependencies: Ollama (dev), OpenRouter, the four BYOK provider APIs, Google and GitHub OAuth apps, Neon, Azure.

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
| AUTH-7 | Email/password accounts must confirm their email before using documents or Q&A; users can request a password reset by email. Emails are sent via Gmail SMTP. | M |
| AUTH-8 | Users can delete their account; this deletes their documents, chunks, and stored key. | M |

### 3.2 Document management (DOC)
| ID | Requirement | Pri |
|---|---|---|
| DOC-1 | Users can upload a PDF (text-based), .docx, .txt or .md file. | M |
| DOC-2 | Unsupported or invalid files are rejected with a clear error (type checked by content, not only by extension). | M |
| DOC-3 | Per-user limits are enforced: demo users max 3 documents, 5 MB per file; BYOK users max 100 documents, 25 MB per file. Values are configuration. If a user removes their key while above the demo limits, existing documents stay but new uploads are blocked until they are under the limit. | M |
| DOC-4 | Processing (extract → chunk → embed → store) runs in the background; the upload call returns immediately with a document ID and status. | M |
| DOC-5 | Users can see each document's status: `Pending`, `Processing`, `Ready`, `Failed` (with reason). | M |
| DOC-6 | Users can list their documents (name, type, size, page count, chunk count, status, upload date). | M |
| DOC-7 | Users can delete a document; its chunks are deleted too. | M |
| DOC-8 | A PDF with no extractable text is marked `Failed` with a "scanned PDFs are not supported" reason. | M |
| DOC-9 | Original files are discarded after text extraction. | M |
| DOC-10 | Each chunk keeps source metadata: document ID, file name, page number (PDF) or section/heading (docx/md), chunk position. | M |

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
- OpenAPI document published; interactive docs available in development.
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
| Gmail SMTP | Confirmation and password-reset emails (AUTH-7). |

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
- REL-2: Transient provider errors (429/5xx) are retried with backoff a limited number of times.
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
6. The service runs on Azure Container Apps with Neon, within free tiers, using only configuration changes from the local setup.

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
| OI-7 | Email confirmation / reset in v1? | Yes, via Gmail SMTP (D22). |
