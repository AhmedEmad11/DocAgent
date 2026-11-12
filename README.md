# DocAgent

A document Q&A API: users upload documents and ask questions about them. Answers come from retrieval-augmented generation (RAG) driven by an LLM agent, with citations. Users can bring their own LLM provider key; a free OpenRouter model serves a limited demo.

- Requirements: [docs/SRS.md](docs/SRS.md)
- Development plan: [docs/DEVELOPMENT_PLAN.md](docs/DEVELOPMENT_PLAN.md)
- Progress log: [docs/PROGRESS.md](docs/PROGRESS.md)

## Status
Phase 4 (agent workflows) implemented, pending review. See the progress log for details.

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
- Limits travel with the LLM route: `Agent:Limits:Development|Demo|Byok` = `MaxToolCalls` and `TimeoutSeconds` (development uses the BYOK values until the demo/BYOK routes exist).

## Agent evaluation (SRS D32)
`tests/DocAgent.EvalTests` holds a fixed bilingual corpus (`Corpus/`) and the question set (`eval-set.json`: answerable questions of several types, at least 40% Arabic, plus unanswerable ones). It needs Docker, the embedding model and Ollama with `gemma4:12b`, and is skipped in normal `dotnet test` runs (only its grader unit tests run):
```bash
DOCAGENT_OLLAMA_TESTS=1 dotnet test tests/DocAgent.EvalTests --filter "FullyQualifiedName~AgentEvalTests"
```
It prints a results table (id, language, type, pass/fail, latency, tool calls) and the totals against the targets (at least 80% of answerable questions correct with a citation to an expected document; 100% of unanswerable ones answered as not found). Optional environment: `DOCAGENT_EVAL_ONLY=en-01,ar-` (ids or prefixes), `DOCAGENT_EVAL_MODE=SinglePass`, `DOCAGENT_EVAL_OUT=<results file>` (full answers are written next to it as `.answers.json`), `DOCAGENT_OLLAMA_ENDPOINT`/`_MODEL`/`_THINK`.
