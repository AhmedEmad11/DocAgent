# DocAgent

A document Q&A API: users upload documents and ask questions about them. Answers come from retrieval-augmented generation (RAG) driven by an LLM agent, with citations. Users can bring their own LLM provider key; a free OpenRouter model serves a limited demo.

- Requirements: [docs/SRS.md](docs/SRS.md)
- Development plan: [docs/DEVELOPMENT_PLAN.md](docs/DEVELOPMENT_PLAN.md)
- Progress log: [docs/PROGRESS.md](docs/PROGRESS.md)

## Status
Phase 2 (document ingestion) implemented, pending review. See the progress log for details.

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
