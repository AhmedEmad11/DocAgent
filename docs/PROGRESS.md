# Progress Log

Updated after every change. Newest entries at the top.

## Current status
| Item | Value |
|---|---|
| Current phase | Phase 0 complete; Phase 1 not started |
| Next step | Phase 1 (data layer and authentication). Needs from stakeholder first: Gmail app password, Google and GitHub OAuth app credentials |
| Blockers | Waiting for Phase 1 credentials |

## Phase checklist
- [x] SRS approved (v0.2, 2026-11-12)
- [x] Development plan approved (v0.2, 2026-11-12)
- [x] Phase 0: Environment and skeleton (2026-11-12)
- [ ] Phase 1: Data layer and authentication
- [ ] Phase 2: Document ingestion
- [ ] Phase 3: Retrieval and basic Q&A
- [ ] Phase 4: Agent workflows
- [ ] Phase 5: BYOK providers and key management
- [ ] Phase 6: Demo mode
- [ ] Phase 7: Hardening
- [ ] Phase 8: Deployment
- [ ] Phase 9: Next.js frontend

## Log

### 2026-11-12: Phase 0 complete
- Repository: https://github.com/AhmedEmad11/DocAgent (public).
- First CI run (#43248164689): **success**; 4/4 integration tests passed on `ubuntu-latest`. The "Failed to connect to 127.0.0.1:1" log lines come from the test that checks the unhealthy case on purpose.
- CI warnings (not failures): `actions/checkout@v4` and `actions/setup-dotnet@v4` target the deprecated Node.js 20 runtime; `ubuntu-latest` is moving to Ubuntu 26. Not changed; waiting for a stakeholder decision.
- All Phase 0 "done when" conditions met: the AppHost starts the API and Postgres, `/health/ready` is healthy, and CI passes.

### 2026-11-12: Phase 0 verified locally; pushed to GitHub
- Stakeholder fixed Docker (engine now at `unix:///var/run/docker.sock`).
- `dotnet test`: **4 passed, 0 failed** (integration tests against `pgvector/pgvector:pg17` via Testcontainers).
- AppHost verified: started the pgvector container and the API; `GET /health/ready` → 200 `Healthy` (real DB), `/health/live` → 200 `Healthy`, `/openapi/v1.json` → 200. Aspire dashboard served at `http://localhost:15222`.
- Note: the AppHost template sets `AspireUseCliBundle=true`, which makes `dotnet run` go through the Aspire CLI. The CLI tries to trust the HTTPS dev certificate interactively and stalled in a non-interactive shell. The verification ran with `-p:AspireUseCliBundle=false` and `ASPIRE_ALLOW_UNSECURED_TRANSPORT=true` (http profile). For normal interactive use, `dotnet run --project src/DocAgent.AppHost` is unchanged; no project setting was changed.
- The Postgres container is persistent (`ContainerLifetime.Persistent`) and keeps running after the AppHost stops; stop it with `docker stop` if not needed.
- Git author for this repository set (repo-local) to the stakeholder's name and Gmail address, as chosen by the stakeholder.
- Created public GitHub repository `DocAgent` and pushed `main`.

### 2026-11-12: SRS and plan approved; Phase 0 started
- Stakeholder approved SRS v0.2 and development plan v0.2.
- Installed .NET SDK **10.0.404** to `~/.dotnet` with Microsoft's `dotnet-install.sh`. Appended to `~/.bashrc`: `DOTNET_ROOT`, `PATH`, and `DOTNET_CLI_TELEMETRY_OPTOUT=1` (telemetry opt-out was added without asking; remove that line if not wanted).
- Installed Aspire project templates **13.6.5**.
- `git init` (branch `main`); added `.gitignore` and `.editorconfig` from the .NET templates.
- Created `DocAgent.slnx` with: `DocAgent.AppHost`, `DocAgent.ServiceDefaults`, `DocAgent.Api`, `DocAgent.Core`, `DocAgent.Infrastructure`, `DocAgent.UnitTests`, `DocAgent.IntegrationTests` (all `net10.0`). Project references: Api → Core, Infrastructure, ServiceDefaults; Infrastructure → Core; AppHost → Api.
- AppHost: PostgreSQL using image `pgvector/pgvector:pg17`, persistent container with data volume `docagent-postgres-data`, database `docagentdb`; API waits for the DB and is health-checked via `/health/ready`.
- ServiceDefaults: health endpoints renamed to `/health/live` and `/health/ready` (SRS OBS-2) and mapped in **all** environments (needed for Azure Container Apps probes; responses contain only the status word).
- API: removed the template weather sample; added ServiceDefaults, `AddNpgsqlDataSource("docagentdb")` (adds the DB readiness check), OpenAPI (Development only), Problem Details.
- Packages: `Aspire.Hosting.PostgreSQL` 13.6.5, `Aspire.Npgsql` 13.6.5, `Testcontainers.PostgreSql` 4.16.0, `Microsoft.AspNetCore.Mvc.Testing` 10.0.15 (11.0.0 targets .NET 11, so pinned to 10.0.x).
- Integration tests (4): `/health/live` healthy; `/health/ready` healthy with DB; `/health/ready` returns 503 when DB unreachable; pgvector extension can be created on the image. Unit test project has no tests yet.
- CI: `.github/workflows/ci.yml` (restore, build Release, test) on push/PR to `main`. Not run yet; there is no GitHub remote.
- README added.
- `dotnet build`: **succeeded, 0 warnings**.
- `dotnet test`: **4 failed**. Cause: the Docker engine is not reachable (the CLI uses the `desktop-linux` context, whose Windows pipe does not exist), so Testcontainers cannot start. Not a code failure; re-run once Docker is running.

### 2026-11-12: Open questions resolved, SRS and plan updated to v0.2
- Answers recorded as SRS decisions D17–D23: demo requires sign-in; 10 demo questions/day; demo 3 docs × 5 MB, BYOK 100 docs × 25 MB; agent caps demo 4 calls/60 s, BYOK 10 calls/120 s; auto-link external logins for verified emails; email confirmation + password reset in v1 via Gmail SMTP; English + Arabic with `multilingual-e5-small` (int8); name **DocAgent**.
- Plan: public GitHub repo; review checkpoints after Phases 1, 4, 5, 8; Claude installs the .NET 10 SDK via `dotnet-install` in Phase 0.
- SRS: new requirement QA-14 (English/Arabic, answer in question's language); constraint C6 (Gmail daily send limit); AUTH-7 raised to Must.
- Plan: added email auth endpoints; deferred per-phase questions listed in plan §6.
- No code written.

### 2026-11-12: Requirements gathered, docs created
- Collected decisions from the stakeholder (recorded as D1–D16 in SRS §1.4): public multi-tenant with private docs; API first, Next.js later; PDF/docx/txt/md; email+password plus Google and GitHub sign-in; Postgres + pgvector; Microsoft Agent Framework; BYOK for OpenRouter, Anthropic, OpenAI and Gemini; Azure Container Apps + Neon; in-process ONNX embeddings; .NET 10; stateless Q&A; original files not kept; all four agent capabilities in v1.
- Environment check: Docker 29.9.2 and Git installed; Ollama has `gemma4:12b` (capabilities: completion, tools, thinking, vision, audio); **.NET SDK not installed**; project folder is empty and not a git repository.
- Created `docs/SRS.md` (v0.1 draft), `docs/DEVELOPMENT_PLAN.md` (v0.1 draft), and this file.
- Open questions recorded: SRS §9 (OI-1…OI-7) and plan §6 (PQ-1…PQ-3).
- No code written.
