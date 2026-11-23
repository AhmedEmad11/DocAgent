# DocAgent

A document Q&A API: users upload documents and ask questions about them. Answers come from retrieval-augmented generation (RAG) driven by an LLM agent, with citations. Users can bring their own LLM provider key; a free OpenRouter model serves a limited demo.

- Requirements: [docs/SRS.md](docs/SRS.md)
- Development plan: [docs/DEVELOPMENT_PLAN.md](docs/DEVELOPMENT_PLAN.md)
- Progress log: [docs/PROGRESS.md](docs/PROGRESS.md)

## Status
Phase 7 (hardening) implemented; the API is deployed on the SnapDeploy free tier at https://docagent-a6971.containers.snapdeploy.app (OpenAPI at `/openapi/v1.json`; it sleeps after 15 minutes idle and takes about a minute to wake). Azure Container Apps is kept as an alternative. See [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md) and the progress log.

## Prerequisites
- .NET 10 SDK
- Docker (running), used by Aspire for PostgreSQL and by the integration tests
- Ollama with `gemma4:12b` (needed from Phase 3)
- The embedding model files (about 120 MB), downloaded once with `scripts/download-models.sh` (see below)

## Solution layout
| Project | Purpose |
|---|---|
| `src/DocAgent.AppHost` | .NET Aspire orchestration for local development (API + PostgreSQL/pgvector) |
| `src/DocAgent.ServiceDefaults` | OpenTelemetry, health checks, service discovery, HTTP resilience |
| `src/DocAgent.Api` | ASP.NET Core API |
| `src/DocAgent.Core` | Domain models, interfaces, agent and tools |
| `src/DocAgent.Infrastructure` | Database, parsers, embeddings, LLM providers |
| `tests/DocAgent.UnitTests` | Unit tests |
| `tests/DocAgent.EvalTests` | Agent evaluation set (English + Arabic) against the real model; skipped unless `DOCAGENT_OLLAMA_TESTS=1`, plus the grader's unit tests |
| `tests/DocAgent.IntegrationTests` | Integration tests against real PostgreSQL + pgvector (Testcontainers) |

## Embedding model
Document ingestion embeds text in-process with `multilingual-e5-small` (int8 ONNX, Hugging Face `Xenova/multilingual-e5-small`, pinned commit). The files are **not** in git (SRS D30). Download them before running the API or the tests:
```bash
scripts/download-models.sh
```
The script verifies SHA-256 hashes, is safe to re-run, and writes to `models/multilingual-e5-small/` (git-ignored). The API finds that folder from `Embeddings:ModelPath` (relative paths are searched upwards from the content root and the app folder, so `dotnet run`, the tests and a container all work). CI runs the same script and caches `models/`. Tests that need the model fail with a message telling you to run the script when it is missing.

## Run locally
```bash
dotnet run --project src/DocAgent.AppHost
```
This starts PostgreSQL (pgvector) and the API, and prints the Aspire dashboard URL. The PostgreSQL container is persistent and keeps its data in the `docagent-postgres-data` volume.

Health endpoints:
- `GET /health/live`: the process is running
- `GET /health/ready`: the API and its database are ready

## Test
```bash
dotnet test
```

## Ask, summarize, compare (Phase 4)
- `POST /api/v1/ask` answers with an agent (Microsoft Agent Framework) that searches the user's documents with tools, then a separate verification pass removes unsupported claims. Response: `answer, notFound, citations[], mode, provider, model, incomplete, toolCalls, notices[]`. `Qa:Mode=SinglePass` selects the Phase 3 retrieve-and-prompt path (also the fallback when the agent loop breaks).
- `POST /api/v1/ask/stream` is the same as Server-Sent Events: `searching`, `tool_call` (tool name only), `verifying`, then `answer` (the JSON above) or `error`.
- `POST /api/v1/documents/{id}/summary` (body optional: `{"focus": "..."}`) and `POST /api/v1/documents/compare` (`{"documentIdA", "documentIdB", "focus"?}`) run map-reduce over the document's chunks.
- Limits travel with the LLM route: `Agent:Limits:Development|Demo|Byok` = `MaxToolCalls` and `TimeoutSeconds`. BYOK routes use `Byok`, the local development route uses `Development`; `Demo` is for demo users (below).
- Verification fails closed: if the verifier gives no usable reply after one retry, the answer is withheld and the response is `notFound` (logged as a warning).

## Your own LLM key (BYOK, Phase 5)
Users can bring a key for **OpenRouter, Anthropic, OpenAI or Gemini**. All endpoints need a confirmed email.
| Method | Route | Purpose |
|---|---|---|
| `PUT` | `/api/v1/me/llm-key` | `{provider, model, apiKey}`. Validated with one small chat call (16 tokens max) to the provider, then saved encrypted. Returns `{provider, model, last4, updatedAt}`. |
| `GET` | `/api/v1/me/llm-key` | The saved `{provider, model, last4, updatedAt}`, or 404. The key is never returned. |
| `DELETE` | `/api/v1/me/llm-key` | Remove the key (204, also when none). Deleting the account removes it too. |
| `GET` | `/api/v1/providers/{provider}/models` | Tool-calling models for the provider. The key comes from the `X-Llm-Api-Key` header (so a model can be chosen before the first save; a header because URLs get logged), otherwise from the saved key if it belongs to that provider. |

- **Routing (KEY-6):** with a saved key every `/ask`, `/ask/stream`, summary and compare request of that user goes to their provider and model with the `Agent:Limits:Byok` limits; responses report `mode: "byok"` and the real `provider`/`model`. Without a key: in Development/Testing the local Ollama model (`gemma4:12b`, `Agent:Limits:Development`); in other environments the OpenRouter demo (next section). Switch: `Llm:NoKeyRoute` = `Auto` (default; Ollama in Development/Testing, Demo elsewhere) | `Ollama` | `Demo` | `None` (users must bring a key: 403 `llm_key_required`).
- **Providers:** OpenRouter, OpenAI and Gemini use the OpenAI client (`Microsoft.Extensions.AI.OpenAI`) against their fixed OpenAI-compatible endpoints (`https://openrouter.ai/api/v1/`, `https://api.openai.com/v1/`, `https://generativelanguage.googleapis.com/v1beta/openai/`); Anthropic uses the official `Anthropic` SDK. Endpoints are constants, users cannot send a base URL (KEY-8); the HTTP layer refuses any other host, never follows redirects, and the SDKs' own retries are off.
- **Model lists:** OpenRouter reports tool support per model. The other APIs do not, so: Anthropic = all `claude-*`; Gemini = `gemini-*` with `generateContent`, minus embedding/tts/image/live variants; OpenAI = `gpt-*`, `chatgpt-*`, `o1/o3/o4/o5` minus audio/realtime/transcribe/tts/image/search/instruct/codex variants. The save does not check that the chosen model is in the list (the validation call proves the key can use it).
- **Errors (KEY-7)**, Problem Details with a stable `code`; texts are fixed, nothing from the provider's response is copied:
  | Cause | On save | On ask/summary/compare | `code` |
  |---|---|---|---|
  | Invalid key (401/403, Gemini 400 `API_KEY_INVALID`) | 400 | 422 | `llm_key_invalid` |
  | No credit / quota (402, OpenAI `insufficient_quota`, Anthropic "credit balance") | 402 | 402 | `llm_no_credit` |
  | Rate limited (429) | 429 + `Retry-After` if given | same | `llm_rate_limited` |
  | Provider down / network (5xx, 529, timeout) | 503 | 503 | `llm_unavailable` |
  | Unknown model (404, "model not found") | 400 | 422 | `llm_model_unknown` |
  | Other rejection | 502 | 502 | `llm_request_rejected` |
  | No key and `Llm:NoKeyRoute=None` | - | 403 | `llm_key_required` |
  | Saved key cannot be decrypted | - | 409 | `llm_key_unreadable` |
  In `/ask/stream` the status is already 200 when the provider answers, so a provider failure arrives as an `error` event with `status`, `title`, `code`.
- **Secrets (SEC-1, SEC-2):** keys are encrypted with ASP.NET Core Data Protection (purpose `DocAgent.UserLlmKey.v1`, sub-purpose per user, so a ciphertext copied to another row does not decrypt). The default key ring location is used in development; **a persistent, separately stored key ring is a Phase 8 decision** (without it a container restart makes saved keys unreadable and users must save them again). Provider calls use plain `HttpClient`s without IHttpClientFactory logging or resilience handlers, so no header is logged; provider error responses are reduced to a fixed message before they travel; Microsoft.Extensions.AI telemetry has sensitive data off.
- **Rate limit:** `PUT` and the models endpoint share 10 calls per minute per user (`RateLimiting:LlmKey`).
- **Live smoke tests:** all provider tests use mocked HTTP. `tests/DocAgent.UnitTests/Llm/LiveProviderSmokeTests.cs` runs against the real provider when `DOCAGENT_LIVE_OPENROUTER_KEY`, `DOCAGENT_LIVE_ANTHROPIC_KEY`, `DOCAGENT_LIVE_OPENAI_KEY` or `DOCAGENT_LIVE_GEMINI_KEY` is set (optional `DOCAGENT_LIVE_<PROVIDER>_MODEL`), otherwise they are skipped. Each makes a few cheap calls (validation, model list, a tool call, a wrong-key check).

## Demo mode (Phase 6)
Signed-in users **without a saved key** are served by a free model on OpenRouter, paid by no one: the operator's key. Anonymous visitors have no access (D17); users with a key always use it (KEY-6).
- **Models:** `google/gemma-4-31b-it:free` (primary) and `google/gemma-4-26b-a4b-it:free` (fallback), both configuration (`Llm:Demo:Model`, `Llm:Demo:FallbackModel`). On 429, 5xx, timeout or "model not found" from the primary, the same request is repeated once on the fallback and stays there; the response's `model` is the one that answered. Both busy: 429 `demo_rate_limited` with `Retry-After`; both down: 503 `demo_unavailable`.
- **Operator key (never in git):** `Llm:Demo:OpenRouterApiKey`. In development:
  ```bash
  dotnet user-secrets set "Llm:Demo:OpenRouterApiKey" "<key>" --project src/DocAgent.Api
  ```
  In production it is a Container Apps secret (Phase 8). The same protections as users' keys apply (fixed host, no redirects, no header logging, error bodies dropped). A key the provider rejects (401/402/403) is an operator problem: the user gets 503 `demo_unavailable`, the log gets an error (`Llm:Demo:OpenRouterApiKey` named, never its value), and the fallback is not tried.
- **When it is on:** `Llm:NoKeyRoute=Auto` (default) = local Ollama in Development/Testing, **Demo elsewhere**. Development can opt in with `Llm:NoKeyRoute=Demo`. Demo needs the operator key: the app **fails at startup** with a clear message if it is missing (set `Llm:NoKeyRoute=None` for a deployment without demo).
- **Daily allowance (DEMO-2):** `Llm:Demo:DailyQuestionLimit` = 10 per user per **UTC calendar day**, kept in the database (`DemoUsages`), so restarts do not reset it. `/ask`, `/ask/stream`, summary and compare each cost one, charged by one conditional upsert before the model is called (parallel requests cannot exceed the limit). Requests rejected before the model call (400/404/409) cost nothing, and a question is given back when the demo models fail before answering anything. When used up: **429 `demo_quota_exceeded`** with `Retry-After` (seconds to the next UTC midnight), `limit`, `resetsAt` and a message to add your own key (DEMO-5). `GET /api/v1/me` shows `demo: {limit, used, remaining, resetsAt, privacyNotice}` (null for users with a key or without a demo route).
- **Reduced limits (DEMO-3):** `Agent:Limits:Demo` (4 tool calls, 60 s) and, for uploads, `Documents:Limits:Demo` (3 documents, 5 MB; BYOK 100 / 25 MB). A user who removes their key keeps existing documents but cannot upload above the demo limit.
- **Shared limit for the operator key (Phase 7):** `Llm:Demo:GlobalRequestsPerMinute` = 20 demo requests per minute across **all** users (sliding window, per process; 0 = off). The free OpenRouter models have per-minute and per-day limits that every demo user shares; without a local limit a few users could exhaust them for everyone and each request would still travel to OpenRouter to be refused. It runs before the daily allowance is charged and before any provider call, so a refused request costs the user nothing: 429 `demo_rate_limited` with `Retry-After`. One request is one ask, stream, summary or comparison, and each makes several model calls, so the right value depends on the free models' current per-minute limit.
- **Privacy (PRIV-1):** every demo answer (ask, stream, summary, compare) starts its `notices[]` with a notice that the question and the document excerpts go to a free third-party model via OpenRouter whose provider may log and use them; `GET /api/v1/me` carries the same text.
- **Errors:**
  | Cause | Status | `code` |
  |---|---|---|
  | Daily allowance used up | 429 + `Retry-After` | `demo_quota_exceeded` |
  | Both demo models rate limited, or the shared request limit (below) is used up | 429 + `Retry-After` | `demo_rate_limited` |
  | Both demo models down, or operator key/model setting wrong | 503 | `demo_unavailable` |
- **Not verified live:** all demo tests use mocked HTTP. `LiveDemoSmokeTests` (`tests/DocAgent.UnitTests/Llm/LiveProviderSmokeTests.cs`) runs a tool-calling round trip against both models when `DOCAGENT_LIVE_DEMO_OPENROUTER_KEY` is set (optional `DOCAGENT_LIVE_DEMO_MODEL`), otherwise it is skipped.

## Security and hardening (Phase 7)
The review (OWASP API Security Top 10, 2023) with every finding, its status and test is in [docs/SECURITY_REVIEW.md](docs/SECURITY_REVIEW.md). What it added to the running API (all configuration, safe defaults):
- **Auth decisions are explicit:** `tests/DocAgent.IntegrationTests/EndpointInventory.cs` lists every mapped endpoint and whether it is public, signed-in or needs a confirmed email; `HardeningTests` fails when an endpoint is added without an entry or its protection differs.
- **Resource limits (API4):** JSON bodies are limited to `Limits:MaxJsonBodyBytes` (256 KB, 413 Problem Details; the upload has its own limit by tier); per-user **concurrency** caps `RateLimiting:Upload:MaxConcurrentPerUser` (2) and `RateLimiting:Ask:MaxConcurrentPerUser` (4, shared by ask, stream, summary and compare), 429 with `Retry-After` (0 turns a cap off); `Documents:Ingestion:MaxChunksPerDocument` (2000, about 3 MB of text): a longer document is marked `Failed` with a message to split it. This also gives retrieval a defined maximum: 100 documents x 2000 chunks = 200,000 chunks for one user (PERF-2, below).
- **Headers (SEC-4):** `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`, `Content-Security-Policy: default-src 'none'; frame-ancestors 'none'`, `Cache-Control: no-store`, `Permissions-Policy`; no `Server` header; HSTS (1 year, subdomains) outside Development.
- **Behind a proxy:** set `ForwardedHeaders:Enabled=true` when the API runs behind a TLS-terminating proxy that is the only way in (Azure Container Apps ingress). It trusts `X-Forwarded-For`/`-Proto`, so HSTS sees HTTPS and the auth rate limit sees the client IP instead of the proxy's. On by default in `appsettings.Production.json`; leave it off otherwise: anyone could spoof their IP.
- **CORS:** off unless `Cors:AllowedOrigins` lists exact origins (Development: `http://localhost:3000`, for the Next.js UI). No wildcard (refused at startup), no credentials (bearer tokens).
- **Host names:** `AllowedHosts` must name the API's public host name(s) in production; outside Development/Testing the app refuses to start while it is `*` or empty (Identity builds the confirmation link from the request's `Host` header).
- **OpenAPI** (`/openapi/v1.json`): served in every environment, anonymously and read-only (there is no interactive UI). Every operation has a summary, description, all status codes (Problem Details), the Bearer scheme and, for the main requests, examples; `OpenApiTests` checks this.
- **Retries (REL-2):** the local Ollama client retries connection failures and 502/503/504 (`Llm:Ollama:Retries` 2, doubling delay from 250 ms); the database uses Npgsql's retrying execution strategy; confirmation/reset emails time out after `Email:Smtp:TimeoutSeconds` (10) and retry a dropped connection or temporary 4xx once (`MaxAttempts` 2); ingestion jobs retry up to `MaxAttempts` (3) with backoff, interrupted ones are recovered at start and, while running, jobs idle longer than `StaleJobMinutes` (30) are taken back. Calls to the hosted LLM providers (BYOK and demo) get exactly **one** retry on a 5xx (incl. Anthropic 529) or a connection failure, after 500 ms or `Retry-After` (capped at 2 s); a 429 or other 4xx is never retried and neither is a response already streaming. It is done in one place, `ProviderHttpHandler` (the SDKs' own retries stay off). In demo mode the primary gets that one retry, then the fallback model answers; a 429 goes straight to the fallback; the daily allowance is charged once per question, not per HTTP call.
- **Total chunk cap (DOC-3):** besides 2,000 chunks per document, one user may hold `Documents:Limits:Byok:MaxTotalChunks` = 20,000 chunks in all (demo: `Demo:MaxTotalChunks` = 3,000). Checked by the ingestion worker before embedding and again under the per-user advisory lock when storing (so parallel workers cannot exceed it); a document over the cap is `Failed` with "Storage limit reached: N of LIMIT chunks used ...". Delete documents to free space. `GET /api/v1/me` returns `chunks: { used, limit }`. The PERF-2 test below still seeds 200,000 chunks, a deliberately larger worst case than the cap allows.

### Retrieval load test (PERF-2)
`tests/DocAgent.IntegrationTests/RetrievalLoadTests.cs` is skipped unless `DOCAGENT_PERF_TESTS=1`. It seeds one user at the maximum (100 documents x 2000 chunks, 384-dim synthetic vectors generated inside PostgreSQL) plus 100,000 chunks of ten other users in a Testcontainers pgvector, builds the HNSW and full-text indexes, then measures p50/p95 of `Retriever.SearchAsync` (real ONNX query embedding included) for vector-only, hybrid and document-scoped queries, and prints an `EXPLAIN ANALYZE`. It fails when p95 is over `DOCAGENT_PERF_P95_MS` (1000). Other knobs: `DOCAGENT_PERF_CHUNKS_PER_DOC`, `_DOCS`, `_OTHER_CHUNKS`, `_QUERIES`, `DOCAGENT_PERF_OUTPUT=<file>`. Needs Docker and the embedding model; takes several minutes and a few GB of disk.
```bash
DOCAGENT_PERF_TESTS=1 dotnet test tests/DocAgent.IntegrationTests --filter "FullyQualifiedName~RetrievalLoadTests"
```

## Deploy (Phase 8)
Primary target: the **SnapDeploy free tier** (no credit card): the API runs from this repository's `Dockerfile` in a 512 MB / 0.25 vCPU container (sleeps after 15 minutes without traffic, 100 container-hours a month), with a Neon Postgres database, email through Brevo SMTP and the OpenRouter demo model. Azure Container Apps (Bicep + GitHub workflow, scale to zero) is kept as an alternative. Step by step (accounts, the exact environment variables and which are secrets, first deploy, OAuth apps, smoke test, limits, memory numbers): **[docs/DEPLOYMENT.md](docs/DEPLOYMENT.md)**.
- `Dockerfile`: two stages and under 3,000 bytes (SnapDeploy free-tier limits, checked in CI), builds for the host's architecture (SnapDeploy is arm64), non-root, the embedding model is downloaded and SHA-256 verified at build time, tuned for 512 MB (workstation GC, small embedding batches, no ONNX memory arena). Listens on `PORT` when the host sets it (SnapDeploy), otherwise 8080. `docker build -t docagent-api .`
- Migrations: `Database__MigrateOnStartup=true` applies them at startup under a Postgres advisory lock (hosts without a pre-deploy hook, SnapDeploy); a failure stops the startup with a clear message. Default `false` in Production. `docker run --rm -e ConnectionStrings__docagentdb=... docagent-api --migrate` applies them and exits (the Azure deploy step, or a one-off run).
- `infra/main.bicep` + `infra/main.bicepparam`, `.github/workflows/deploy.yml`: the Azure path (Container Apps, min 0 / max 1 replica, probes, secrets; after CI on `main` it builds, pushes, migrates, deploys). It skips itself cleanly until the Azure and Neon settings exist and is not used for SnapDeploy.
- In Production the app **refuses to start** without: `AllowedHosts` set to the public host (not `*`), the operator OpenRouter key, and the Data Protection certificate (`DataProtection__CertificatePfxBase64` / `__CertificatePassword`). The Data Protection key ring (which encrypts users' stored LLM keys) is kept in the database and encrypted with that certificate; Development keeps the default local key files and needs no certificate.
- Behind a TLS-terminating edge: `ForwardedHeaders__Enabled` (Production default `true`), `ForwardedHeaders__ForwardLimit`, and `ForwardedHeaders__AssumeHttps` (for an edge that sends no `X-Forwarded-Proto`) are configurable.

## Agent evaluation (SRS D32)
`tests/DocAgent.EvalTests` holds a fixed bilingual corpus (`Corpus/`) and the question set (`eval-set.json`: answerable questions of several types, at least 40% Arabic, plus unanswerable ones). It needs Docker, the embedding model and Ollama with `gemma4:12b`, and is skipped in normal `dotnet test` runs (only its grader unit tests run):
```bash
DOCAGENT_OLLAMA_TESTS=1 dotnet test tests/DocAgent.EvalTests --filter "FullyQualifiedName~AgentEvalTests"
```
It prints a results table (id, language, type, pass/fail, latency, tool calls) and the totals against the targets (at least 80% of answerable questions correct with a citation to an expected document; 100% of unanswerable ones answered as not found). Optional environment: `DOCAGENT_EVAL_ONLY=en-01,ar-` (ids or prefixes), `DOCAGENT_EVAL_MODE=SinglePass`, `DOCAGENT_EVAL_OUT=<results file>` (full answers are written next to it as `.answers.json`), `DOCAGENT_OLLAMA_ENDPOINT`/`_MODEL`/`_THINK`.
