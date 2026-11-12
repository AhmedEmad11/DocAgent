# Progress Log

Updated after every change. Newest entries at the top.

## Current status
| Item | Value |
|---|---|
| Current phase | Phase 2: Document ingestion (starting) |
| Next step | Implement Phase 1, review, stakeholder checkpoint |
| Blockers | None |

## Phase checklist
- [x] SRS approved (v0.2, 2026-11-12)
- [x] Development plan approved (v0.2, 2026-11-12)
- [x] Phase 0: Environment and skeleton (2026-11-12)
- [x] Phase 1: Data layer and authentication (2026-11-12)
- [ ] Phase 2: Document ingestion
- [ ] Phase 3: Retrieval and basic Q&A
- [ ] Phase 4: Agent workflows
- [ ] Phase 5: BYOK providers and key management
- [ ] Phase 6: Demo mode
- [ ] Phase 7: Hardening
- [ ] Phase 8: Deployment
- [ ] Phase 9: Next.js frontend

## Log

### 2026-11-12: Phase 1 checkpoint passed
- Stakeholder verified real Google and GitHub sign-in end to end via `https://localhost:7090/dev/external-login-test` (after updating the GitHub OAuth app callback to `https://localhost:7090/signin-github`).
- Stakeholder decisions recorded as SRS D28 (remove password on auto-link of unconfirmed account), D29 (log and continue on email send failure), D30 (ONNX model downloaded at build time, pinned + SHA-256 checked).
- Phase 1 committed and pushed.

### 2026-11-12: Dev external sign-in switched to HTTPS
- Stakeholder's browser test of `/dev/external-login-test` returned 400 "returnUrl is missing or not allowed" for both providers. Cause (verified): in Development the API redirects `http://localhost:5248` → `https://localhost:7090` (`UseHttpsRedirection`), so the page sent an https return URL that was not allowlisted.
- Stakeholder decision: use HTTPS in development (matches production).
- `appsettings.Development.json`: allowlist entry changed to `https://localhost:7090/dev/external-login-test`.
- SETUP_CREDENTIALS.md: GitHub OAuth app Homepage/callback changed to `https://localhost:7090` / `https://localhost:7090/signin-github` (stakeholder must update the GitHub app). Google already had the https redirect URI.
- SRS D24 updated with the https test page URL.

### 2026-11-12: Phase 1 reviewed by main session (checkpoint pending)
- Independent re-run: `dotnet build` 0 errors / 0 warnings; `dotnet test` **77 passed** (30 unit, 47 integration).
- Code review of the security-relevant parts (external account resolution, one-time code issue/redeem, return-URL validation, tenant query filters, EmailConfirmed policy, rate limiter, email sender, startup migration): no blocking issues found.
- Plan §3 API surface updated to the actual routes (Identity API names such as `confirmEmail`, plus `external/callback`, `external/exchange`, `/dev/external-login-test`, `GET /api/v1/me`).
- Items for stakeholder decision at the checkpoint: (a) removing the password of an *unconfirmed* account when it is auto-linked to a verified Google/GitHub login (security hardening not in the SRS); (b) email send failures are logged and swallowed rather than failing the request.
- Not yet verified: a real Google/GitHub browser round trip (needs the stakeholder).
- Phase 1 changes are not committed yet; waiting for the checkpoint.

### 2026-11-12: Phase 1 implemented (pending review)
- **Packages added** (net10.0): Core: Microsoft.Extensions.Identity.Stores 10.0.15, Pgvector 0.3.2. Infrastructure: Microsoft.AspNetCore.Identity.EntityFrameworkCore 10.0.15, Npgsql.EntityFrameworkCore.PostgreSQL 10.0.3, Pgvector.EntityFrameworkCore 0.3.0, Microsoft.EntityFrameworkCore.Design 10.0.15, MailKit 4.18.2 (5.0.0 has a breaking `SendAsync` API, so 4.x was kept), plus a FrameworkReference to Microsoft.AspNetCore.App (for `IEmailSender<T>`). Api: Aspire.Npgsql.EntityFrameworkCore.PostgreSQL 13.6.5 (replaces Aspire.Npgsql), Microsoft.AspNetCore.Authentication.Google 10.0.15, AspNet.Security.OAuth.GitHub 10.1.0. Local tool: dotnet-ef 10.0.15 (`dotnet-tools.json`).
- **Migration**: `20261112163510_InitialCreate` (Infrastructure/Migrations): `vector` extension, Identity tables, Documents, Chunks (`vector(384)` + HNSW `vector_cosine_ops`), IngestionJobs, UserLlmCredentials, ExternalLoginCodes (unique CodeHash). Applied at startup only in Development/Testing (15 s bound, failure logged and ignored).
- **Endpoints**: `/api/v1/auth/*` (Identity API: register, login, refresh, confirmEmail, resendConfirmationEmail, forgotPassword, resetPassword, manage/*), `GET /api/v1/auth/external/{google|github}`, `GET /api/v1/auth/external/callback`, `POST /api/v1/auth/external/exchange`, `GET/DELETE /api/v1/me`, `GET /dev/external-login-test` (Development only). Policy `EmailConfirmed` defined, applied to nothing yet. Auth rate limit 10/min/IP (config `RateLimiting:Auth:*`).
- **Other**: tenant query filters (Document, Chunk, IngestionJob, UserLlmCredential) via `ICurrentUser`; MailKit `SmtpEmailSender`; Mailpit container (`axllent/mailpit:v1.33.1`) in the AppHost; `DocAgent.Api.http` updated.
- **Tests**: 30 unit + 47 integration, all passing (`dotnet test`), build 0 warnings.
- **AppHost verification** (run, then stopped): `/health/ready` = Healthy; Mailpit container up; `POST /register` produced 1 message ("Confirm your DocAgent email") visible via Mailpit `/api/v1/messages`; `/external/google` and `/external/github` returned 302 to accounts.google.com and github.com; disallowed returnUrl gave 400 Problem Details; anonymous `/api/v1/me` gave 401; `/dev/external-login-test` 200. A real Google/GitHub round trip (consent screen, callback, code exchange) was NOT exercised: needs a browser.
- **Deviations / choices**: (1) `AddNpgsqlDbContext` registers a pooled DbContext that cannot take the scoped `ICurrentUser`; it failed at runtime under Development scope validation. Used `AddDbContext` + `EnrichNpgsqlDbContext` instead. (2) Aspire's EF health check was disabled and replaced by `DatabaseHealthCheck` (plain connection, no retry) because Aspire's retry strategy made the unreachable-DB check take about 70 s. (3) `IngestionJob` has a denormalized `UserId` (needed for its query filter). (4) When an unconfirmed password account is auto-linked to a verified external login, its password is removed (blocks pre-registration takeover). (5) `SmtpEmailSender` logs and swallows send failures so registration does not 500. (6) Rate limit is one shared counter per IP across the listed endpoints; behind a proxy, forwarded headers must be configured (Phase 8). (7) Test factory enables `ValidateScopes`.

### 2026-11-12: Phase 1 started
- Stakeholder set the 4 OAuth user-secrets; verified present and non-empty without printing values (Google client ID has the expected `.apps.googleusercontent.com` suffix).
- Phase 1 decisions recorded as SRS D24–D27: one-time-code token handoff for external sign-in (dev return URLs: `http://localhost:3000` + built-in API test page); Identity default password rules; 1 h access / 14 d refresh tokens; auth rate limit 10/min per IP.
- Implementation delegated to a Sonnet subagent (per stakeholder preference), with review by the main session afterwards.

### 2026-11-12: Email changed from Gmail SMTP to Mailpit (dev); production provider deferred
- Gmail app passwords are not available on the stakeholder's Google account ("The setting you are looking for is not available for your account").
- Stakeholder decision: use **Mailpit** (local email catcher in the Aspire AppHost) for development; choose the free-tier production email provider in Phase 8.
- SRS updated: D22, C6, AUTH-7, external services table, OI-7.
- Plan updated: stack table (SMTP via MailKit behind `IEmailSender`), Phase 1 needs and tasks, Phase 8 task, risks, deferred questions (new: Phase 8 email provider).
- SETUP_CREDENTIALS.md: removed the Gmail section; only the Google and GitHub OAuth credentials (4 keys) are needed now.
- Stakeholder installed nothing new; earlier in this session RTK's global Claude hook was installed (`rtk init -g --auto-patch`) at the stakeholder's request; it takes effect after restarting Claude Code.

### 2026-11-12: CI actions upgraded, graphify-out ignored, credential setup guide
- CI: `actions/checkout@v4` → `@v7` (v7.0.1, 2026-07-20) and `actions/setup-dotnet@v4` → `@v6` (v6.1.0, 2026-11-03); both run on Node.js 24, which removes the Node 20 deprecation warning.
- `.gitignore`: added `graphify-out/` (local knowledge-graph output).
- CI run 43250851902 on commit `6720f4c`: **success**, 4/4 tests passed, build 0 warnings, no Node.js deprecation warnings.
- API project: `dotnet user-secrets init` (adds `UserSecretsId` to `DocAgent.Api.csproj`).
- Added [SETUP_CREDENTIALS.md](SETUP_CREDENTIALS.md): steps for the Gmail app password, Google OAuth client and GitHub OAuth app. It fixes the configuration key names Phase 1 will read: `Email:Smtp:Username`, `Email:Smtp:Password`, `Email:FromAddress`, `Authentication:Google:ClientId/ClientSecret`, `Authentication:GitHub:ClientId/ClientSecret`. Dev callback URLs: `/signin-google` (http 5248 and https 7090), `/signin-github` (http 5248).

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
