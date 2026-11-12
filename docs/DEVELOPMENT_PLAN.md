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
- Review checkpoints with the stakeholder after Phases 1, 4, 5 and 8 (PQ-3). Other phases continue without stopping, unless a new question comes up.

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
| Email | Gmail SMTP with an app password (MailKit) | Yes (daily send limit) |
| Parsing | PdfPig (PDF), DocumentFormat.OpenXml (docx), Markdig (md) | Yes |
| Tokenizing/chunking | Microsoft.ML.Tokenizers + custom recursive splitter | Yes |
| Background work | `BackgroundService` + Postgres-backed job table | Yes |
| Rate limiting | ASP.NET Core rate limiting middleware | Yes |
| Observability | OpenTelemetry + Aspire Dashboard (dev) | Yes |
| Tests | xUnit, Testcontainers (Postgres + pgvector) | Yes |
| Hosting | Azure Container Apps (free grant) + Neon (free tier) | Yes, within limits |
| CI | GitHub Actions on a public repository | Yes |
| Frontend (later) | Next.js | Yes |

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
| POST | `/api/v1/auth/register` | Email/password sign-up | AUTH-1 |
| POST | `/api/v1/auth/login` | Email/password sign-in | AUTH-2 |
| POST | `/api/v1/auth/refresh` | Refresh token | AUTH-6 |
| GET | `/api/v1/auth/external/{google\|github}` | Start external sign-in | AUTH-3/4 |
| GET | `/api/v1/auth/confirm-email` | Confirm email from the emailed link | AUTH-7 |
| POST | `/api/v1/auth/resend-confirmation` | Resend the confirmation email | AUTH-7 |
| POST | `/api/v1/auth/forgot-password` | Send a password-reset email | AUTH-7 |
| POST | `/api/v1/auth/reset-password` | Set a new password with the reset code | AUTH-7 |
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
**Needs from stakeholder before starting:** a Gmail account + app password for sending; Google and GitHub OAuth app credentials for local development (setup steps will be provided).
- EF Core model: Identity tables, Document, Chunk (vector column + HNSW index), IngestionJob, UserLlmCredential.
- Migrations, plus enabling the pgvector extension.
- Identity bearer-token endpoints; Google and GitHub external login with automatic linking for provider-verified emails (AUTH-5).
- Email confirmation and password reset via Gmail SMTP (AUTH-7); unconfirmed accounts blocked from documents and Q&A.
- Per-user data filter (EF global query filter on `UserId`), as required by SEC-3.
- Auth rate limiting (SEC-8). Account deletion (AUTH-8).
- **Tests:** registration/login, external-login flow (mocked), tenant isolation.
- **Done when:** a user can register/sign in all three ways, confirm email, reset password, and call an authorized endpoint.
- **Checkpoint:** stakeholder review.

### Phase 2: Document ingestion
- Upload endpoint: content-type sniffing, size limits, per-user quotas (demo 3 docs / 5 MB; BYOK 100 docs / 25 MB).
- Parsers: PdfPig (page-aware), OpenXml (heading-aware), Markdig/plain text. Detect scanned PDFs (DOC-8).
- Chunker: about 500–800 tokens with overlap; keeps page/section metadata.
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
- Resilience: retry/backoff policies (REL-2).
- OpenAPI completeness and examples.

### Phase 8: Deployment (Azure Container Apps + Neon)
- Dockerfile (multi-stage, includes the ONNX model).
- Neon project with pgvector; migrations run as a deploy step.
- Azure Container Apps environment sized to stay within the free grant; secrets in Container Apps secrets.
- Data Protection key ring persisted outside the container (location decided in this phase, e.g. stored in the database, encrypted).
- GitHub Actions: build → test → push image → deploy.
- Register production OAuth redirect URIs with Google and GitHub.
- Gmail SMTP app password stored as a Container Apps secret.
- **Done when:** acceptance criteria SRS §8 pass against the deployed service.
- **Checkpoint:** stakeholder review.

### Phase 9: Next.js frontend (after the API is finished)
- Planned separately once the API is complete; it will get its own SRS section and plan.
- Expected scope: sign-in (email, Google, GitHub), document upload/list, ask with streaming and citations, key settings, demo banner.

---

## 5. Risks

| Risk | Impact | Mitigation |
|---|---|---|
| `gemma4:12b` and the demo model handle tools differently from BYOK models | Inconsistent agent behavior | Evaluation set run against each model; simple, strict tool schemas; capped loops |
| Free OpenRouter models withdrawn or rate limited | Demo outage | Model IDs in config; fallback model; clear user messages |
| Neon free storage (0.5 GB) fills up | Uploads fail | Per-user quotas (D19); store only text + embeddings; storage monitoring |
| Gmail SMTP daily limit or account lock | Confirmation/reset emails stop | Email sender behind an interface so a provider can be swapped by configuration |
| Arabic PDF text extracted in wrong order | Poor Arabic answers | Test in Phase 2; report results before continuing |
| Azure free grant exceeded | Unexpected cost | Scale-to-zero, small CPU/memory, budget alert in Azure |
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
| 1 | Gmail account/app password and OAuth app credentials (stakeholder supplies) |
| 2 | Ship the ONNX model inside the image, or download it on first start? |
| 4 | Accuracy level the evaluation set must reach |
| 5 | Which providers the stakeholder can provide test keys for |
| 6 | Which free OpenRouter model(s) to use (picked from the then-current list) |
