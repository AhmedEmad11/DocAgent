# DocAgent

A document Q&A API: users upload documents and ask questions about them. Answers come from retrieval-augmented generation (RAG) driven by an LLM agent, with citations. Users can bring their own LLM provider key; a free OpenRouter model serves a limited demo.

- Requirements: [docs/SRS.md](docs/SRS.md)
- Development plan: [docs/DEVELOPMENT_PLAN.md](docs/DEVELOPMENT_PLAN.md)
- Progress log: [docs/PROGRESS.md](docs/PROGRESS.md)

## Status
Phase 5 (BYOK providers and key management) implemented, pending review. See the progress log for details.

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
- Limits travel with the LLM route: `Agent:Limits:Development|Demo|Byok` = `MaxToolCalls` and `TimeoutSeconds`. BYOK routes use `Byok`, the local development route uses `Development`; `Demo` is for Phase 6.
- Verification fails closed: if the verifier gives no usable reply after one retry, the answer is withheld and the response is `notFound` (logged as a warning).

## Your own LLM key (BYOK, Phase 5)
Users can bring a key for **OpenRouter, Anthropic, OpenAI or Gemini**. All endpoints need a confirmed email.
| Method | Route | Purpose |
|---|---|---|
| `PUT` | `/api/v1/me/llm-key` | `{provider, model, apiKey}`. Validated with one small chat call (16 tokens max) to the provider, then saved encrypted. Returns `{provider, model, last4, updatedAt}`. |
| `GET` | `/api/v1/me/llm-key` | The saved `{provider, model, last4, updatedAt}`, or 404. The key is never returned. |
| `DELETE` | `/api/v1/me/llm-key` | Remove the key (204, also when none). Deleting the account removes it too. |
| `GET` | `/api/v1/providers/{provider}/models` | Tool-calling models for the provider. The key comes from the `X-Llm-Api-Key` header (so a model can be chosen before the first save; a header because URLs get logged), otherwise from the saved key if it belongs to that provider. |

- **Routing (KEY-6):** with a saved key every `/ask`, `/ask/stream`, summary and compare request of that user goes to their provider and model with the `Agent:Limits:Byok` limits; responses report `mode: "byok"` and the real `provider`/`model`. Without a key: in Development/Testing the local Ollama model (`gemma4:12b`, `Agent:Limits:Development`); in other environments **403 `llm_key_required`** ("add your own key") until Phase 6 adds the OpenRouter demo route. Switch: `Llm:NoKeyRoute` = `Auto` (default; Ollama in Development/Testing, none elsewhere) | `Ollama` | `None`.
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
  | No key outside Development | - | 403 | `llm_key_required` |
  | Saved key cannot be decrypted | - | 409 | `llm_key_unreadable` |
  In `/ask/stream` the status is already 200 when the provider answers, so a provider failure arrives as an `error` event with `status`, `title`, `code`.
- **Secrets (SEC-1, SEC-2):** keys are encrypted with ASP.NET Core Data Protection (purpose `DocAgent.UserLlmKey.v1`, sub-purpose per user, so a ciphertext copied to another row does not decrypt). The default key ring location is used in development; **a persistent, separately stored key ring is a Phase 8 decision** (without it a container restart makes saved keys unreadable and users must save them again). Provider calls use plain `HttpClient`s without IHttpClientFactory logging or resilience handlers, so no header is logged; provider error responses are reduced to a fixed message before they travel; Microsoft.Extensions.AI telemetry has sensitive data off.
- **Rate limit:** `PUT` and the models endpoint share 10 calls per minute per user (`RateLimiting:LlmKey`).
- **Live smoke tests:** all provider tests use mocked HTTP. `tests/DocAgent.UnitTests/Llm/LiveProviderSmokeTests.cs` runs against the real provider when `DOCAGENT_LIVE_OPENROUTER_KEY`, `DOCAGENT_LIVE_ANTHROPIC_KEY`, `DOCAGENT_LIVE_OPENAI_KEY` or `DOCAGENT_LIVE_GEMINI_KEY` is set (optional `DOCAGENT_LIVE_<PROVIDER>_MODEL`), otherwise they are skipped. Each makes a few cheap calls (validation, model list, a tool call, a wrong-key check).

## Agent evaluation (SRS D32)
`tests/DocAgent.EvalTests` holds a fixed bilingual corpus (`Corpus/`) and the question set (`eval-set.json`: answerable questions of several types, at least 40% Arabic, plus unanswerable ones). It needs Docker, the embedding model and Ollama with `gemma4:12b`, and is skipped in normal `dotnet test` runs (only its grader unit tests run):
```bash
DOCAGENT_OLLAMA_TESTS=1 dotnet test tests/DocAgent.EvalTests --filter "FullyQualifiedName~AgentEvalTests"
```
It prints a results table (id, language, type, pass/fail, latency, tool calls) and the totals against the targets (at least 80% of answerable questions correct with a citation to an expected document; 100% of unanswerable ones answered as not found). Optional environment: `DOCAGENT_EVAL_ONLY=en-01,ar-` (ids or prefixes), `DOCAGENT_EVAL_MODE=SinglePass`, `DOCAGENT_EVAL_OUT=<results file>` (full answers are written next to it as `.answers.json`), `DOCAGENT_OLLAMA_ENDPOINT`/`_MODEL`/`_THINK`.
