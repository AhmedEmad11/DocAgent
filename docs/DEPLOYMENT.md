# Deployment (Phase 8): Azure Container Apps + Neon

Status: **the repository side is ready; nothing has been deployed.** There is no Azure subscription yet. Every command below that talks to a cloud service is for you to run once the account exists. Nothing in this repository calls Azure, Neon, Brevo or OpenRouter by itself except the deploy workflow, and that skips itself until you configure it.

What runs where:

| Part | Service | Cost model |
|---|---|---|
| API (the Docker image) | Azure Container Apps, Consumption plan, 0 to 1 replica, default `*.azurecontainerapps.io` URL | free monthly grant, scale to zero |
| Database (Postgres + pgvector) | Neon free tier | free, 0.5 GB |
| Confirmation / reset email | Brevo SMTP relay (`smtp-relay.brevo.com:587`, STARTTLS) | free tier (300 emails/day) |
| Demo LLM | OpenRouter, free models, operator key | free |
| Image registry | GitHub Container Registry (public package) | free for public images |
| Logs | Log Analytics workspace (optional) | free allowance, then pay per GB (capped) |

The pieces in the repository:

| File | Purpose |
|---|---|
| `Dockerfile`, `.dockerignore` | Multi-stage image, non-root, port 8080, embedding model baked in (downloaded at build time, SHA-256 verified) |
| `infra/main.bicep`, `infra/main.bicepparam` | Log Analytics (optional), Container Apps environment, the Container App |
| `.github/workflows/deploy.yml` | Build and push image, run migrations, deploy |
| `src/DocAgent.Api/appsettings.Production.json` | Production defaults, no secrets |
| `DocAgent.Api --migrate` | The migration step (same image) |

## 1. How a deployment works

```
push to main -> CI green -> Deploy workflow
   1. build image, push ghcr.io/<owner>/docagent-api:<git sha>
   2. docker run <image> --migrate           (against Neon; the app never migrates itself in Production)
   3. az deployment group create (Bicep)     (new revision with that image)
   4. smoke test /health/live and /health/ready through the public URL
```

Migrations run **before** the new revision starts, with the same image, so the new code never meets an old schema. Migrations must therefore be backward compatible for one revision (add columns/tables first, remove later), because the old revision serves traffic until the new one is ready.

The workflow does nothing until these exist (it reports "Deploy skipped, missing ..." and finishes green): the secrets `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, `NEON_CONNECTION_STRING` and the variable `AZURE_RESOURCE_GROUP`. The full list is in section 11.

## 2. Create the accounts (you do this once)

### 2.1 Azure free account
1. Go to https://azure.microsoft.com/free and sign up (needs a phone number and a card for identity checks; the free account does not charge the card unless you upgrade to pay-as-you-go).
2. Install the Azure CLI (https://learn.microsoft.com/cli/azure/install-azure-cli) and sign in: `az login`.
3. Check the subscription: `az account show --query "{name:name, id:id, tenant:tenantId}" -o table`. You need the **subscription id** and **tenant id** later.
4. Register the providers Container Apps needs (once per subscription):
   ```bash
   az provider register --namespace Microsoft.App
   az provider register --namespace Microsoft.OperationalInsights
   ```
   Wait until `az provider show -n Microsoft.App --query registrationState -o tsv` prints `Registered`.

### 2.2 Resource group
```bash
az group create --name docagent-rg --location westeurope   # pick a region near you / near the Neon region
```
Use one resource group for everything: deleting it removes everything this project created in Azure (`az group delete --name docagent-rg`).

### 2.3 GitHub to Azure without a stored password (OIDC federated credential)
The deploy workflow logs in with a short-lived token that GitHub issues, so no Azure secret is stored in GitHub.

```bash
# 1. An app registration (service principal) for the workflow
az ad app create --display-name docagent-github-deploy --query appId -o tsv          # prints the client id; call it $CLIENT_ID
az ad sp create --id $CLIENT_ID

# 2. Let the workflow, and only the "production" environment of this repository, sign in as that app
az ad app federated-credential create --id $CLIENT_ID --parameters '{
  "name": "github-production",
  "issuer": "https://token.actions.githubusercontent.com",
  "subject": "repo:<GITHUB_OWNER>/<REPO>:environment:production",
  "audiences": ["api://AzureADTokenExchange"]
}'

# 3. Permission: Contributor on the resource group only (not the subscription)
az role assignment create --assignee $CLIENT_ID --role Contributor \
  --scope /subscriptions/<SUBSCRIPTION_ID>/resourceGroups/docagent-rg
```
Replace `<GITHUB_OWNER>/<REPO>` with the repository (for example `ahmed/DocAgent`). The deploy job declares `environment: production`, which is what the `subject` matches; create that environment in the repository (Settings, Environments, New environment `production`). You can add yourself as a required reviewer there if you want a manual approval before every deployment.

### 2.4 Neon (database)
1. Sign up at https://neon.com (free plan, no card).
2. Create a project (region close to the Azure region; Postgres 17 to match the tests). Database name: `docagentdb`.
3. pgvector: nothing to enable by hand. The first migration runs `CREATE EXTENSION vector` and Neon allows it for the project owner role. (If it ever fails, run `CREATE EXTENSION IF NOT EXISTS vector;` in Neon's SQL editor.)
4. Copy the connection details from the dashboard ("Connect"): use the **direct** connection (host without `-pooler`). The app uses transaction-scoped advisory locks and a normal connection pool; the pooled PgBouncer endpoint is not needed with one replica and could change session behaviour.
5. Write it in Npgsql key-value form (the dashboard shows a `postgresql://` URL; the .NET provider needs this form):
   ```
   Host=ep-xxxx.<region>.aws.neon.tech;Database=docagentdb;Username=<role>;Password=<password>;SSL Mode=VerifyFull;Gss Encryption Mode=Disable;Maximum Pool Size=10;Timeout=30
   ```
   - `SSL Mode=VerifyFull` verifies Neon's certificate with the image's CA bundle. If a connection test fails on certificate validation, use `SSL Mode=Require;Trust Server Certificate=true` as the fallback (still encrypted, but not verified).
   - `Gss Encryption Mode=Disable` stops Npgsql from looking for Kerberos libraries that the slim image does not have (otherwise a harmless "Cannot load library libgssapi_krb5.so.2" line is printed).
   - `Timeout=30`: Neon suspends an idle database after 5 minutes (free plan) and needs a few seconds to wake it; the first request after a pause is slower, not failed.
   - This string is the secret `NEON_CONNECTION_STRING`. Never paste it into chat or commit it.

### 2.5 Brevo (email) and the spam caveat
1. Sign up at https://www.brevo.com (free plan).
2. **Senders & IP / Senders**: add one sender, a single email address you can receive mail at, and click the verification link Brevo sends to it. There is no domain yet, so the address is a personal or mailbox address (for example a Gmail address).
3. **SMTP & API, SMTP**: note the **SMTP login** (looks like `xxxxxx@smtp-brevo.com`) and create an **SMTP key**. These are `SMTP_USERNAME` and `SMTP_PASSWORD`. The relay is `smtp-relay.brevo.com`, port 587, STARTTLS (already the Production default in `appsettings.Production.json`).
4. The verified address becomes the variable `EMAIL_FROM_ADDRESS`.

Spam risk, stated plainly: without your own domain there is no domain authentication (SPF/DKIM/DMARC for the visible From domain). Mail "from" a Gmail/Outlook address that is sent through Brevo's servers is often put in spam or rejected by the recipient's provider, because that provider sees a message claiming to be from its own domain sent by someone else. Expect confirmation and reset emails to land in the spam folder; tell testers to check there. The fix is a domain you own (SPF, DKIM and DMARC records at Brevo, a sender such as `noreply@yourdomain`), which is a product/cost decision (see "Questions" in PROGRESS). Brevo's free plan also limits sending to about 300 emails per day, and it can add a Brevo footer.

### 2.6 OpenRouter operator key
Create a key at https://openrouter.ai/keys (the same account used for the demo models). This is `OPENROUTER_API_KEY`. Set a spend limit of zero on the key if the dashboard offers one: only `:free` models are used.

### 2.7 Data Protection certificate
The app stores its Data Protection key ring in the database, encrypted by a certificate you create once. The ring encrypts every user's stored LLM key, so: **keep the `.pfx` and its password, and never change them casually.** If the certificate is lost, all stored user LLM keys and all refresh tokens become unreadable (users sign in again and re-enter their keys; nothing else is lost).

```bash
mkdir -p ~/docagent-secrets && cd ~/docagent-secrets && umask 077
openssl req -x509 -newkey rsa:2048 -nodes -keyout dp.key -out dp.crt \
  -subj "/CN=DocAgent data protection" -days 3650
PFX_PASSWORD="$(openssl rand -base64 24)"
openssl pkcs12 -export -inkey dp.key -in dp.crt -out dp.pfx -passout "pass:$PFX_PASSWORD"
base64 -w0 dp.pfx > dp.pfx.b64          # DP_CERT_PFX_BASE64 (about 3.5 KB)
echo "$PFX_PASSWORD"                    # DP_CERT_PASSWORD: copy it into the password manager and the GitHub secret
rm dp.key                               # the .pfx holds the private key; keep dp.pfx somewhere safe (password manager / encrypted backup)
```
Never put these files in the repository (`*.pfx` is in `.dockerignore`; add `~/docagent-secrets` nowhere near the repo). Rotation is not automated: a new certificate cannot read keys written under the old one, so replacing it means users re-enter their LLM keys. Ten years of validity makes this a non-event for this project.

## 3. GitHub repository settings

Settings, Secrets and variables, Actions.

**Secrets**
| Name | Value |
|---|---|
| `AZURE_CLIENT_ID` | app registration client id (section 2.3) |
| `AZURE_TENANT_ID` | tenant id |
| `AZURE_SUBSCRIPTION_ID` | subscription id |
| `NEON_CONNECTION_STRING` | the Npgsql string (section 2.4) |
| `OPENROUTER_API_KEY` | operator key |
| `DP_CERT_PFX_BASE64` | contents of `dp.pfx.b64` |
| `DP_CERT_PASSWORD` | the PFX password |
| `SMTP_USERNAME`, `SMTP_PASSWORD` | Brevo SMTP login and key |
| `OAUTH_GOOGLE_CLIENT_SECRET`, `OAUTH_GITHUB_CLIENT_SECRET` | production OAuth secrets (section 5; leave unset on the first deploy) |

**Variables** (not secret)
| Name | Value |
|---|---|
| `AZURE_RESOURCE_GROUP` | `docagent-rg` |
| `EMAIL_FROM_ADDRESS` | the verified Brevo sender |
| `OAUTH_GOOGLE_CLIENT_ID`, `OAUTH_GITHUB_CLIENT_ID` | production OAuth client ids (section 5) |

(GitHub does not allow secret names starting with `GITHUB_`, hence the `OAUTH_` prefix.)

**Environment** `production` (section 2.3). **Package visibility:** the first deployment pushes `ghcr.io/<owner>/docagent-api` as a *private* package. Open it (GitHub profile, Packages, docagent-api, Package settings) and set visibility to **Public** so the Container App can pull it without credentials (the repository is public, so this is free). Do this after the first workflow run pushes the image, before the app can start; if the first deploy finished before that, just re-run it.

## 4. First deployment (two steps because of the OAuth redirect URLs)

The public URL is `https://<app>.<random>.<region>.azurecontainerapps.io`. Its random part exists only after the environment is created, and the Google and GitHub OAuth apps need that URL as their redirect URI. So:

**Step 1: deploy without Google/GitHub sign-in.** Email/password sign-in works; external providers are simply not registered when their settings are absent (the app starts without them).
1. Make sure the secrets and variables of section 3 are set (without the `OAUTH_*` ones).
2. Run the workflow: Actions, Deploy, Run workflow. (After merging this repository state to `main` it also runs by itself after CI.)
3. Make the GHCR package public (section 3) if needed and re-run.
4. Read the URL from the run summary, or:
   ```bash
   az deployment group show -g docagent-rg -n main --query properties.outputs.url.value -o tsv
   ```
5. Run the smoke test (section 6) for everything that does not need OAuth.

**Step 2: production OAuth apps** (needs the URL from step 1; `<URL>` below is for example `https://docagent-api.happyhill-1a2b3c.westeurope.azurecontainerapps.io`)
- **Google** (https://console.cloud.google.com/apis/credentials): create a new OAuth client of type Web application named `DocAgent prod`. Authorized redirect URI: `<URL>/signin-google`. Publish the consent screen to Production when you want users other than test users (basic scopes need no review). Copy client id and secret.
- **GitHub** (https://github.com/settings/developers): new OAuth App `DocAgent (prod)`. Homepage `<URL>`, callback `<URL>/signin-github`. Generate a client secret.
- In the repository set variables `OAUTH_GOOGLE_CLIENT_ID`, `OAUTH_GITHUB_CLIENT_ID` and secrets `OAUTH_GOOGLE_CLIENT_SECRET`, `OAUTH_GITHUB_CLIENT_SECRET`, then re-run the Deploy workflow. A provider is enabled only when both its id and its secret are set.
- Until the web UI exists (Phase 9) `allowedReturnUrls` in `infra/main.bicepparam` is empty, so the external sign-in code hand-off has no allowed target; set it to the UI origin when the UI is deployed (and `corsOrigins` likewise). Keep the development client ids for local work: the production ones are separate.

## 5. Settings reference (production)

All of these arrive as environment variables on the Container App (the Bicep template sets them; secrets are Container Apps secrets, referenced by name, never shown in the portal).

| Setting | Source | Notes |
|---|---|---|
| `ASPNETCORE_ENVIRONMENT=Production` | image | selects `appsettings.Production.json` |
| `AllowedHosts` | Bicep (app FQDN) | the app refuses to start in Production with `*` or empty |
| `ConnectionStrings__docagentdb` | secret | Neon |
| `Llm__NoKeyRoute` | `appsettings.Production.json`: `Demo` | users without a key use the OpenRouter demo; Ollama is never used |
| `Llm__Demo__OpenRouterApiKey` | secret | required at startup |
| `DataProtection__CertificatePfxBase64`, `__CertificatePassword` | secrets | required at startup in Production |
| `Email__Smtp__Host/Port/UseStartTls` | `appsettings.Production.json` | Brevo, 587, STARTTLS |
| `Email__Smtp__Username/Password` | secrets | Brevo |
| `Email__FromAddress`, `Email__FromName` | Bicep parameters | the single verified sender |
| `Authentication__Google__*`, `Authentication__GitHub__*` | Bicep parameters / secrets | optional (step 2) |
| `Authentication__External__AllowedReturnUrls__N` | Bicep `allowedReturnUrls` | UI origins |
| `Cors__AllowedOrigins__N` | Bicep `corsOrigins` | UI origins, never `*` |
| `ForwardedHeaders__Enabled=true` | `appsettings.Production.json` | see below |
| `HttpsRedirection__Enabled=false` | `appsettings.Production.json` | see below |

**Forwarded headers (S-20).** The ingress terminates TLS and adds `X-Forwarded-For` and `X-Forwarded-Proto`. The app trusts them (limit 1, meaning the single entry the ingress appended, which the client cannot forge) so the auth rate limit sees real client IPs and HSTS is sent. The known-proxy and known-network lists are cleared because the ingress address is not fixed and changes; that is safe only because the container accepts traffic solely from the Container Apps ingress (the only exposed port is the ingress target port 8080, and no other route to the container exists). Do not run a Production-configured container anywhere it is directly reachable from the internet.

**HTTPS (S-15).** The ingress has `allowInsecure: false`, so plain `http://` requests are redirected to HTTPS before they reach the app. The app's own HTTPS redirect is switched off in Production because the in-cluster health probes use plain HTTP and have nothing to redirect to. The app still sends HSTS (1 year) on requests the ingress marks as HTTPS. The `*.azurecontainerapps.io` suffix is not on the HSTS preload list, so HSTS protects returning browsers only after the first visit.

**Health probes.** Startup and liveness use `/health/live`, readiness uses `/health/ready` (includes the database). Probes arrive with the pod IP as the `Host` header, which `AllowedHosts` would reject, so the Bicep template sends the public FQDN as the probe's `Host` header.

## 6. Smoke test after a deployment (maps to SRS section 8)

Use your deployment URL as `$URL`. These are the acceptance checks; a checkmark means a person has run it against the live deployment. **None has been run yet.**

| # | Check | SRS |
|---|---|---|
| 1 | `curl $URL/health/live` and `/health/ready` answer `Healthy`. The first call after idle takes several seconds (cold start). | OBS-2 |
| 2 | `curl -i $URL/health/live`: has `Strict-Transport-Security`, no `Server` header; `curl -i http://<same host>/health/live` redirects to https. | SEC-4 |
| 3 | `curl -H "Host: evil.example" $URL/health/live` is rejected (400) when sent straight to the host, and `$URL/openapi/v1.json` is served. | S-18 |
| 4 | Register with a real email, receive the confirmation mail (check spam), confirm, sign in. | AC 1, D22 |
| 5 | Sign in with Google and with GitHub (after step 2 of section 4; needs the UI return URL, or call `/api/v1/auth/external/...` as in the API docs and read the redirect). | AC 1, AUTH-3/4 |
| 6 | Upload one file of each type (pdf, docx, md/txt), wait for `Ready`. The first ingestion after a cold start loads the embedding model (see section 7 for timing). | AC 1 |
| 7 | Ask a question: answer with citations (demo mode, OpenRouter free model). | AC 1, AC 4 |
| 8 | Summarize and compare two documents: results include citations. | AC 5 |
| 9 | Second account: cannot see, cite, list or delete the first account's documents (404/empty). | AC 2 |
| 10 | Demo limits: the 11th question of the UTC day returns 429 `demo_quota_exceeded`; a 4th document is refused. | AC 4 |
| 11 | Save a BYOK key (`PUT /api/v1/me/llm-key`), ask: the answer comes from that provider; delete the key: back to demo. Then **create a new revision (re-run the workflow) and ask again with the saved key**: it must still decrypt (the key ring survives revisions). | AC 3, SEC-1 |
| 12 | Auth rate limit: 11 wrong logins in a minute from one machine give 429, while a second machine/network is not limited (forwarded headers work). | SEC-8 |
| 13 | Azure portal: Container App shows min 0 / max 1 replicas, one active revision; Neon dashboard shows storage well under 0.5 GB and the compute suspending when idle. Monthly usage in Cost Management stays within the free grant. | AC 6 |
| 14 | `az containerapp logs show -g docagent-rg -n docagent-api --follow` (or Log Analytics) shows structured request logs and no secrets. | OBS-1 |

Acceptance criterion 6 ("only configuration changes from the local setup") is met by construction: the same image runs locally and on Azure; only the settings above differ.

## 7. Sizing, cold start and cost

**Container size.** `infra/main.bicep` defaults to **0.5** vCPU and **1 GiB**. Measured locally with `docker run --memory 1g --cpus 0.5` and `docker stats` / cgroup `memory.peak` (Production environment, Postgres in a second container):

| Moment | Memory |
|---|---|
| idle after start (model not loaded yet) | about 60 MiB |
| peak while ingesting a 350 KB Markdown file (400 chunks; model loaded, tokenizer, batches) | about 490 MiB (470 MiB resident afterwards) |
| ingestion time for that file at 0.5 vCPU | about 85 s (about 5 chunks/s) |

So 1 GiB leaves about 2x headroom over the measured peak, and 0.5 GiB would not. A maximal document (2,000 chunks, the per-document cap) takes about 7 minutes at 0.5 vCPU; `1.0` vCPU / `2Gi` halves that and costs the same vCPU-seconds, so raise `cpu`/`memory` in `infra/main.bicepparam` if ingestion feels slow (valid Consumption pairs: 0.25/0.5, 0.5/1, 0.75/1.5, 1/2). The embedding model alone is 140 MB on disk (int8 ONNX). Image size: 470 MB uncompressed, about 215 MB as a compressed layer set (the registry stores and the environment pulls the compressed form).

A finding that cost real time: ONNX Runtime sizes its thread pool from the host's cores, not from the container's CPU limit, so with 0.5 vCPU a dozen spinning threads were throttled 96% of the time and the same file had not finished after 8 minutes. The embedder now sizes its pool from .NET's cgroup-aware `Environment.ProcessorCount` and disables worker spinning (`Embeddings:IntraOpThreads` overrides it).

**Scale to zero.** The app scales to zero replicas after about 5 minutes without HTTP traffic. The next request starts a replica (image pull is cached on the node usually; expect roughly 5 to 15 seconds for the container plus .NET start, plus a few seconds if Neon was also suspended). The first caller after idle waits; clients should use a generous timeout and retry. Setting `minReplicas` to 1 in `infra/main.bicepparam` removes the cold start but uses the free grant continuously: 0.5 vCPU x 730 h x 3,600 s = about 1.3 million vCPU-seconds a month, far over the free 180,000 vCPU-seconds, so it would cost money.

**Background ingestion and scale to zero.** Ingestion runs inside the API process and HTTP traffic is what keeps the replica alive. A user who uploads and then closes the browser can see the replica scale down mid-ingestion. The job is not lost: on the next start the worker recovers interrupted jobs and processes them again. The UI (Phase 9) should keep polling the document status, which also keeps the app awake.

**Free grant (Container Apps Consumption, per subscription per month; check the current numbers on the Azure pricing page, they have changed before):** 180,000 vCPU-seconds, 360,000 GiB-seconds and 2 million requests. At 0.5 vCPU and 1 GiB that is about 100 hours of active replica time a month. Idle time at zero replicas is free.

**Not free by default, so watch these:**
- **Log Analytics**: billed per GB ingested after its free allowance. The template caps ingestion at 1 GB/day and keeps 30 days (the minimum). To avoid it completely set `logsMode = 'none'` in `infra/main.bicepparam`; logs are then only readable live with `az containerapp logs show`.
- **Egress** beyond the free allowance, and any resource you create by hand.
- Create a budget with an alert in Cost Management (Azure portal, Cost Management, Budgets, for example 5 EUR) before the first deploy. Budgets alert, they do not stop spending.

**Neon free:** 0.5 GB storage per project, compute scales to zero after 5 minutes idle. 200,000 chunks for one user would need about 1.6 GB (see PROGRESS, PERF-2), so the per-user chunk caps (3,000 demo, 20,000 BYOK) are what keep a handful of users inside 0.5 GB; the total across all users is not capped. That is a product question (see PROGRESS).

**Max one replica (do not raise).** `maxReplicas: 1` is deliberate: the ingestion worker and its job recovery assume one instance, and the auth, ask/upload concurrency and shared demo-key throttles are in-process counters, so two replicas would double every limit and race on job claims.

## 8. Rollback

Each deployment creates a new revision named after the Container App with a suffix; in single-revision mode the previous revision is kept inactive.

```bash
az containerapp revision list -g docagent-rg -n docagent-api -o table
az containerapp revision activate -g docagent-rg -n docagent-api --revision <previous-revision-name>
```
or re-run the Deploy workflow by hand with the input `image-tag` set to an older commit SHA (it skips the build and migrations are a no-op when the schema is already current).

The database does not roll back: migrations only move forward. That is why every migration must keep the previous revision working (additive first). If a migration itself is wrong, fix forward with a new migration.

## 9. Running the migration step by hand

```bash
docker run --rm -e ConnectionStrings__docagentdb="$NEON_CONNECTION_STRING" ghcr.io/<owner>/docagent-api:<sha> --migrate
```
Exit code 0 means the database is current; 1 means failure (message printed, never the connection string); 2 means no connection string. It is safe to run repeatedly.

## 10. Running the image locally (to check a build)

```bash
docker build -t docagent-api .
# needs a pgvector Postgres reachable as in the connection string, a certificate (section 2.7 commands) and a fake or real operator key
docker run --rm -p 8080:8080 \
  -e ConnectionStrings__docagentdb="Host=host.docker.internal;Database=docagentdb;Username=postgres;Password=..." \
  -e AllowedHosts=localhost -e Llm__Demo__OpenRouterApiKey=fake \
  -e DataProtection__CertificatePfxBase64="$(cat dp.pfx.b64)" -e DataProtection__CertificatePassword="$PFX_PASSWORD" \
  docagent-api
curl localhost:8080/health/live
```

## 11. Checklist

- [ ] Azure account, `az login`, providers registered, resource group
- [ ] Budget alert
- [ ] OIDC app registration, federated credential, Contributor on the resource group
- [ ] GitHub environment `production`
- [ ] Neon project, connection string tested
- [ ] Brevo sender verified, SMTP key
- [ ] OpenRouter operator key
- [ ] Data Protection certificate created and backed up
- [ ] GitHub secrets and variables set (section 3)
- [ ] First deploy; GHCR package public; URL noted
- [ ] OAuth apps created with the URL, ids and secrets set, redeploy
- [ ] Smoke test (section 6)
- [ ] Phase 8 acceptance checkpoint
