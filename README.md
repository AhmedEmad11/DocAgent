# DocAgent

A document Q&A API: users upload documents and ask questions about them. Answers come from retrieval-augmented generation (RAG) driven by an LLM agent, with citations. Users can bring their own LLM provider key; a free OpenRouter model serves a limited demo.

- Requirements: [docs/SRS.md](docs/SRS.md)
- Development plan: [docs/DEVELOPMENT_PLAN.md](docs/DEVELOPMENT_PLAN.md)
- Progress log: [docs/PROGRESS.md](docs/PROGRESS.md)

## Status
Phase 0 (project skeleton). See the progress log for details.

## Prerequisites
- .NET 10 SDK
- Docker (running), used by Aspire for PostgreSQL and by the integration tests
- Ollama with `gemma4:12b` (needed from Phase 3)

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
