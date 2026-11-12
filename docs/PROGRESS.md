# Progress Log

Updated after every change. Newest entries at the top.

## Current status
| Item | Value |
|---|---|
| Current phase | Phase 3: Retrieval and basic Q&A (implemented, pending review) |
| Next step | Implement Phase 1, review, stakeholder checkpoint |
| Blockers | None |

## Phase checklist
- [x] SRS approved (v0.2, 2026-11-12)
- [x] Development plan approved (v0.2, 2026-11-12)
- [x] Phase 0: Environment and skeleton (2026-11-12)
- [x] Phase 1: Data layer and authentication (2026-11-12)
- [x] Phase 2: Document ingestion (2026-11-12)
- [x] Phase 3: Retrieval and basic Q&A (2026-11-12)
- [ ] Phase 4: Agent workflows
- [ ] Phase 5: BYOK providers and key management
- [ ] Phase 6: Demo mode
- [ ] Phase 7: Hardening
- [ ] Phase 8: Deployment
- [ ] Phase 9: Next.js frontend

## Log

### 2026-11-12: Phase 3 reviewed and committed
- Independent re-run: build 0 warnings; **256 tests passed** (1 real-Ollama test skipped without `DOCAGENT_OLLAMA_TESTS=1`).
- Reviewed retrieval tenant safety: vector and full-text queries both go through the filtered `db.Chunks`/`db.Documents` (no raw SQL except `SET LOCAL hnsw.iterative_scan`). No blocking issues.
- Note: the AppHost path was not run by the subagent (stakeholder's AppHost was using the shared containers); the API was verified standalone with real Ollama.

### 2026-11-12: Phase 3 implemented (pending review)
- **Packages (Infrastructure):** `OllamaSharp` 5.5.2, `Microsoft.Extensions.AI` 10.11.0 (pipeline builder, OpenTelemetry and logging middleware; `Microsoft.Extensions.AI.Abstractions` 10.11.0 already present). No new test packages.
- **Files:** Core `Retrieval/IRetriever.cs` (`IRetriever`, `RetrievedChunk`). Infrastructure `Retrieval/` (`Retriever`, `RetrievalOptions`, `ReciprocalRankFusion`, `FullTextQuery`, DI), `Llm/` (`ILlmClientFactory`, `LlmRoute`/`LlmRouteInfo`, `ChatClientPipeline`, `OllamaChatClientFactory`, `LlmOptions`, DI), `Qa/` (`QaService`, `QaPromptBuilder`, `CitationParser`, `QuoteTrimmer`, `QaOptions`, DI). Api `Qa/AskEndpoints.cs`, `Program.cs` wiring, `appsettings.json` (`Llm`, `Retrieval`, `Qa`, log level `Microsoft.Extensions.AI`=Warning), ServiceDefaults (`DocAgent.*` trace sources and meters), AppHost (passes `Llm__Ollama__Endpoint`, default `http://localhost:11434`, override with `Ollama:Endpoint` in AppHost config or `Ollama__Endpoint` env var; Ollama is not containerized).
- **Endpoint:** `POST /api/v1/ask` (EmailConfirmed policy). Body `{question (required, <=2000), documentIds?}`. 400 for a bad question or unusable document ids (not yours / not Ready / missing, all reported identically), 409 when the user has no Ready document (checked before the id check), 503 "The language model is unavailable.", 504 on `Qa:TimeoutSeconds` (default 120). Response `{answer, notFound, citations[{index, documentId, fileName, pageNumber, section, quote}], mode, provider, model}`; Phase 3 reports `development` / `Ollama` / configured model through `LlmRouteInfo` from `ILlmClientFactory` (Phases 5-6 plug in there).
- **Migration:** `AddChunkFullTextIndex` (GIN expression index `IX_Chunks_Text_fts` on `to_tsvector('simple', "Text")`).
- **Retrieval design:** query embedded as `query: <question>`; cosine search through `AppDbContext` (tenant filter always on) joined to Ready documents, optional document-id filter, top `4 x TopK` candidates; each query runs in a transaction with `SET LOCAL hnsw.iterative_scan = strict_order` so filters cannot starve an HNSW scan. Results are capped by `Retrieval:TopK` (8), `MinScore` and `MaxContextTokens` (3000, summed stored `TokenCount`, first chunk always kept). `Score` is the cosine similarity.
- **HNSW check (EXPLAIN, `RetrieverIndexTests`, two users x 1500 chunks):** the query shape *can* use `IX_Chunks_Embedding` ("Index Scan using IX_Chunks_Embedding, Order By: Embedding <=> ..., Filter: UserId = ...") but, with the btree on `UserId` present, the planner chooses `IX_Chunks_UserId` plus an exact sort of that user's rows, at this size and also with `enable_seqscan=off`. That is correct (exact results) and fast for one tenant's rows; the planner switches to HNSW by itself when it estimates it cheaper (large tables). The test drops `IX_Chunks_UserId` to prove the HNSW path works with the tenant/status filters. Not tuned further; revisit in Phase 7 with a large dataset.
- **Hybrid measurement** (`RetrievalQualityTests`, 28 questions: 10 English paraphrase, 8 English exact-term, 6 Arabic paraphrase, 4 Arabic exact-term; corpus = 8 hand-written docs + 144 near-duplicate distractor paragraphs that differ only in an invoice number / error code / model number / form id; chunks about 1 paragraph each; MinScore 0). Recall@1/3/5/8, all questions: **vector-only 89 / 93 / 96 / 96 %, hybrid 100 / 100 / 100 / 100 %**. Exact-term questions: vector-only English 89/89/100/100, Arabic 33/67/67/67; hybrid 100 % everywhere. Paraphrase questions: 100 % both. Without distractors the corpus was too easy (vector 100 %), so the distractors are the meaningful part. A first hybrid version (OR of all words, `ts_rank_cd`) was worse than vector-only on some questions: Postgres full-text ranking has no IDF, so common words flood the list. The final version uses full text for strong exact evidence only: a chunk must match **all** significant question words, or all words containing a digit (codes, numbers); everything else is left to the vectors. **Default `Retrieval:Hybrid=true`.** Fusion: reciprocal rank fusion, k=60.
- **MinScore:** measured cosine of the answer chunk for the 28 questions: min 0.813, p10 0.832, median 0.873; top-1 for 10 unanswerable questions: median 0.786, max 0.837 (e5 scores are compressed into roughly 0.7-0.95, so the two ranges overlap a little). `Retrieval:MinScore=0.80` (just under the lowest answer score; rejects 6 of 10 unanswerable questions without an LLM call, the model handles the rest); `FullTextMinScore=0.75` for chunks found only by an exact-term match. Based on a small corpus: re-measure with real user documents.
- **Thinking:** gemma4:12b, same 6 questions (3 English, 2 Arabic, 1 unanswerable) through the full pipeline, warm model: **think off 6-11 s per question, think on 17-25 s, 6/6 correct both ways**. Default **`Llm:Ollama:Think=false`** (configurable, null = model default). `Llm:Ollama:NumCtx=8192` is sent with every request (Ollama's own default 4096 would silently truncate long prompts).
- **Prompting:** system prompt (answer only from sources, `[n]` markers, untrusted-data notice, notFound rule, answer in the question's language), sources as `<source index file page section>` blocks after the rules, question last. Document text and file names are neutralized so they cannot close or open a `<source>` block. Reply format: JSON schema `{answer, notFound}` through `ChatResponseFormat.ForJsonSchema` (works with gemma4 on Ollama in all runs); if the reply is not valid JSON it is used as plain answer text. Citations = distinct valid `[n]` markers in the answer (out-of-range markers are removed from the text and dropped), none when `notFound`. Quote = the cited chunk whitespace-collapsed; if longer than 300 chars it starts at the sentence sharing most words with question + the answer sentence holding the marker and adds following sentences up to 300 (a single longer sentence is cut at a word with an ellipsis). No retrieval hit: `notFound` with a fixed message (English, or Arabic when the question has Arabic letters), no LLM call.
- **Logging/telemetry (SEC-2, OBS-1):** chat client wrapped with `UseOpenTelemetry` (sensitive data off) and `UseLogging`; the logging middleware writes message text at Trace level, so `Microsoft.Extensions.AI` is pinned to Warning in appsettings (a unit test shows Information never contains prompt/answer text, and that Trace would). Own log lines contain only counts and timings. Spans/metrics: `DocAgent.Retrieval`, `DocAgent.Qa`, `DocAgent.Llm`.
- **Tests:** 256 passed, 1 skipped (was 201): unit 145 (+Qa: prompt builder, citation parser, quote trimmer, RRF math, full-text query builder, reply parsing, pipeline logging, Ollama route + connection failure + unknown provider), integration 111 + 1 skipped real-model test (new: `RetrieverTests` 7, `AskEndpointTests` 14, `RetrievalQualityTests` 2, `RetrieverIndexTests` 1; the /ask and retriever tests use a fake `IChatClient` registered through the factory's `configureServices`, real ONNX embeddings and real Postgres). Build: 0 warnings.
- **Real-model test** (`OllamaRealModelTests`, runs only with `DOCAGENT_OLLAMA_TESTS=1`, ran locally against gemma4:12b, think off): 6/6 PASS. EN vacation days 25 [25 days cited from handbook.txt; the corpus also holds an Arabic policy that says 30, and the model reported both], EN router address 192.168.7.1, EN investigator Halvorsen, AR annual leave (30 days), AR warranty (3 years), all citing the right document; unanswerable sick-leave question -> `notFound=true`, no citations. Latency 6-11 s per question warm, 60 s for the first (model load).
- **Manual run:** the stakeholder's AppHost was already running (persistent Postgres container and Mailpit shared), so I did not start a second one and did not touch it. Instead I ran the API standalone (`dotnet run`, http://127.0.0.1:5199) against throwaway pgvector and Mailpit containers (removed afterwards): register -> confirm via the Mailpit API -> login -> upload a .txt -> `/api/v1/ask` with the real Ollama client. Results: EN hotel question answered with the right citation (first call 52.7 s incl. model load, then about 1 s), AR question answered in Arabic, "CEO salary" -> notFound without an LLM call, `travel desk extension 5521` found via full-text, foreign/unknown document id -> 400. The API log contained no document text. The AppHost change (`Llm__Ollama__Endpoint`) compiles but was not run.
- **Deviations / notes:** AppHost not run (see above); `Think` defaults to false instead of the model default; empty `documentIds` array is treated as "all documents"; the 409 check runs before the documentIds check.

### 2026-11-12: DOC-11 reviewed and committed; Phase 3 starting
- Phase 2 CI (run 43276709962): success; 198/198 tests; model downloaded with SHA-256 verification (script exits on mismatch) and cached (~83.5 MB).
- DOC-11 independent re-run: build 0 warnings, **201 tests passed**.

### 2026-11-12: DOC-11 Arabic PDF extraction notice implemented (pending review)
- Files changed: `src/DocAgent.Core/Entities/Document.cs` (`ExtractionNotice`), `src/DocAgent.Infrastructure/Data/AppDbContext.cs` (max length 500), `Ingestion/ArabicTextFixer.cs` (`Fix(text, out reordered)`), `Ingestion/ExtractedDocument.cs` (`ArabicReordered`), `Ingestion/PdfTextExtractor.cs`, `Ingestion/IngestionProcessor.cs` (sets the notice, `ArabicReorderedNotice` const), `src/DocAgent.Api/Documents/DocumentEndpoints.cs` (`extractionNotice` in `DocumentResponse`), tests `ArabicPdfTests.cs` and `DocumentIngestionTests.cs`.
- Migration: `AddDocumentExtractionNotice` (nullable `varchar(500)` on `Documents`).
- Tests: 201 passed (198 existing + 3 new unit tests; the integration theory now also asserts the notice for the Chromium PDF and null for all other files).

### 2026-11-12: Arabic PDF support marked as best effort
- Stakeholder decision: keep the heuristic Arabic PDF repair, document it as best effort, and show a notice on documents that needed it. Recorded as SRS D31 and new requirement DOC-11 (`extractionNotice` on document details).
- Implementation delegated to a Sonnet subagent.

### 2026-11-12: Phase 2 reviewed and committed
- CI for the Phase 1 commit `417984c` (run 43270361696): success, 30/30 unit + 47/47 integration.
- Independent re-run of Phase 2: build 0 errors / 0 warnings; **198 tests passed** (111 unit, 87 integration).
- Reviewed document endpoints (tenant filtering, 404 for other users' ids, per-request body-size limit, content sniffing, per-user advisory lock for the count limit), fixture sizes (Noto Naskh Arabic font 244 KB, SIL OFL; Arabic Chromium PDF 24 KB). No blocking issues.
- Open stakeholder decision: Arabic PDF extraction is heuristic (works on Chromium-generated PDFs; untested on Word/LibreOffice/Adobe output, multi-column layouts; combined diacritics dropped).

### 2026-11-12: Phase 2 implemented (pending review)
**Packages (Infrastructure):** Microsoft.ML.OnnxRuntime 1.31.1, Microsoft.ML.Tokenizers 3.0.0, Microsoft.Extensions.AI.Abstractions 10.11.0, PdfPig 0.1.16 (NuGet id `PdfPig`, namespace `UglyToad.PdfPig`; the `UglyToad.PdfPig` id has no stable release), DocumentFormat.OpenXml 3.5.1, Markdig 1.6.0.

**Model (D30):** Hugging Face `Xenova/multilingual-e5-small` at commit `761b726dd34fb83930e26aab4e9ac3899aa1fa78`: `onnx/model_quantized.onnx` (118 MB, dynamic int8), `sentencepiece.bpe.model`, `tokenizer.json`. Chosen over `intfloat/multilingual-e5-small`'s `model_qint8_avx512_vnni.onnx`, which is tied to AVX512-VNNI CPUs. `scripts/download-models.sh` downloads into git-ignored `models/multilingual-e5-small/`, verifies SHA-256 (hashes hard-coded), idempotent. CI runs it with `actions/cache@v6` (Node 24) keyed on the script hash. Output `last_hidden_state` [batch, seq, 384] inspected; mean pooling + L2 normalize done in code. Token ids match the reference `tokenizers` library for 5 sample strings (unit test).

**Endpoints:** `POST /api/v1/documents` (202 + Location, 413/415/400/409), `GET /api/v1/documents`, `GET /api/v1/documents/{id}`, `DELETE /api/v1/documents/{id}`; all `EmailConfirmed`, tenant-filtered, 404 for other users' ids.

**Code:** `src/DocAgent.Infrastructure/Embeddings` (tokenizer, ONNX generator, path resolver), `.../Ingestion` (sniffer, file name sanitizer, PDF/DOCX/MD/TXT extractors, `ArabicTextFixer`, `TextChunker`, `IngestionJobQueue`, `IngestionProcessor`, `IngestionWorker`, DI extension), `src/DocAgent.Api/Documents/DocumentEndpoints.cs`.

**Migration:** `AddDocumentUpload` (staging table `DocumentUploads`: DocumentId PK/FK cascade, `Content bytea`; rows are deleted when a document becomes Ready or Failed).

**Tests:** 198 passing (111 unit, 87 integration; before: 77). New: tokenizer ids, embedding similarity (EN pair 0.848 vs 0.781 unrelated; EN query vs Arabic passage 0.859 vs 0.729 unrelated), sniffer, file names, chunker, extractors, Arabic PDF (Chromium fixture + PdfPig-built), end-to-end upload of PDF/DOCX/TXT/MD in English and Arabic, scanned PDF, 415/400/413/409, demo/BYOK limits, parallel-upload limit race, tenant isolation, unconfirmed user 403, staging deletion, crash recovery, retry/backoff. Build: 0 warnings. Integration test helper change: per-test databases now use short-lived small Npgsql pools and the factory clears pools on dispose (the ingestion worker kept connections open and exhausted Postgres `max_connections` across ~85 per-test databases).

**AppHost verification:** ran the AppHost (http profile), registered and confirmed a user through Mailpit's API, uploaded an English PDF (made by Chromium) and an Arabic `.md`; both reached `Ready` (1 and 2 chunks); upload returned in 0.04 s; a 6 MB file from a demo user got 413 and an `.exe` got 415 over real Kestrel. AppHost stopped afterwards.

**Arabic PDF extraction quality (D13, OI-5):**
- PdfPig returns Arabic as stored in the PDF. A Chromium-made PDF (typical of real PDFs: shaped glyphs in visual order, presentation forms U+FExx) came out reversed and in presentation forms. `ArabicTextFixer` decides per page (letter statistics: definite article "al-", teh marbuta/alef maqsura position; falls back to presentation-form share) whether the text is reversed, then reverses each line, restores the order of Latin words and numbers, mirrors brackets, and maps presentation forms (incl. lam-alef) to standard letters.
- Result on the Chromium fixture: headline, wrapped paragraphs, a mixed Arabic/English/number line and bracketed text come back in correct logical order; an English page with one Arabic word is unchanged; PDFs written in logical order are left alone. This was better than `pdftotext` on the same file (it garbled the lam-alef ligature).
- Limits (heuristic, not a full bidi implementation): only tested with PDFs from Chromium and PdfPig; Word, LibreOffice, Adobe or older Arabic PDFs are untested and could behave differently (for example partly reversed or non-standard font encodings). Combined diacritic glyphs (shadda + fatha) come out as NUL in PdfPig and are dropped, so heavily vowelled text loses marks. Very short Arabic pages carry little statistical evidence. Multi-column Arabic layouts were not tested. Scanned Arabic PDFs fail like any scanned PDF (no OCR). **Stakeholder decision point:** whether this quality is enough for v1 or a sample set of real Arabic PDFs should be supplied for testing.

**Deviations / choices not in the brief:** see the final report of the implementing agent (chunks never cross a page or heading boundary; 409 for the document-count limit; worker recovery fails jobs that already used 3 attempts; extra limits MaxPdfPages 2000 and docx entry cap 2000; TXT/MD must be UTF-8).

### 2026-11-12: Phase 2 started
- Plan updated: chunk size changed from "500–800 tokens" to target ~400 / max 480 / overlap 60 tokens, because `multilingual-e5-small` truncates input beyond 512 tokens. Document full text for summarize/compare will be derived from ordered chunks (resolves SRS §6 DocumentText design detail).
- Phase 2 implementation delegated to a Sonnet subagent; CI check of the Phase 1 commit delegated to a Haiku subagent.

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
