# Credential Setup for Phase 1 (local development)

Phase 1 needs two sets of credentials:

| # | Credential | Used for | SRS |
|---|---|---|---|
| 1 | Google OAuth client | "Sign in with Google" | AUTH-3 |
| 2 | GitHub OAuth app | "Sign in with GitHub" | AUTH-4 |

**Rules for secrets**
- Store them only with `dotnet user-secrets` (commands below). They are saved in `~/.microsoft/usersecrets/`, outside the repository.
- Never commit them, and do not paste them into the chat. Run the commands yourself, then tell Claude "credentials are set".
- These are **development** credentials. Production gets separate ones in Phase 8 (different callback URLs, stored as Azure Container Apps secrets).

Email needs **no credentials** in development: the AppHost runs **Mailpit**, a local email catcher, and every confirmation/reset email shows up in its web UI (the link appears in the Aspire dashboard). The production email provider is chosen in Phase 8.

Development API address (from `src/DocAgent.Api/Properties/launchSettings.json`): `http://localhost:5248`

---

## 1. Google OAuth client

1. Open https://console.cloud.google.com/ and create a project, for example `DocAgent` (top bar → project picker → **New project**).
2. Go to **APIs & Services → OAuth consent screen** (in newer consoles this is **Google Auth Platform**). If asked to configure it:
   - **App name:** `DocAgent`; **User support email:** your email.
   - **Audience:** **External**.
   - **Contact email:** your email. Finish.
   - **Data access / Scopes:** add `openid`, `.../auth/userinfo.email`, `.../auth/userinfo.profile`. These are basic scopes and do not need Google's verification review.
   - While the app is in **Testing** status, only the **test users** you add can sign in. Add your own Google account under **Audience → Test users**.
3. Go to **APIs & Services → Credentials** (or **Google Auth Platform → Clients**) → **Create credentials / Create client** → **OAuth client ID**.
   - **Application type:** Web application
   - **Name:** `DocAgent dev`
   - **Authorized redirect URIs:** add
     - `http://localhost:5248/signin-google`
     - `https://localhost:7090/signin-google`
   - Leave **Authorized JavaScript origins** empty for now (the Next.js UI comes later).
4. Select **Create**. Copy the **Client ID** and **Client secret**.
5. Save them:

```bash
cd ~/dotnet
dotnet user-secrets set "Authentication:Google:ClientId"     "YOUR_CLIENT_ID.apps.googleusercontent.com" --project src/DocAgent.Api
dotnet user-secrets set "Authentication:Google:ClientSecret" "YOUR_CLIENT_SECRET" --project src/DocAgent.Api
```

---

## 2. GitHub OAuth app

1. Open https://github.com/settings/developers → **OAuth Apps** → **New OAuth App**.
2. Fill in:
   - **Application name:** `DocAgent (dev)`
   - **Homepage URL:** `https://localhost:7090`
   - **Authorization callback URL** (may be labelled **Redirect URI**): `https://localhost:7090/signin-github`
     (A GitHub OAuth app allows only one callback host and port. Development uses HTTPS because the API redirects `http://localhost:5248` to `https://localhost:7090`.)
   - Leave **Enable Device Flow** unchecked.
3. Select **Register application**.
4. On the app page, copy the **Client ID**, then select **Generate a new client secret** and copy it (it is shown once).
5. Save them:

```bash
cd ~/dotnet
dotnet user-secrets set "Authentication:GitHub:ClientId"     "YOUR_CLIENT_ID" --project src/DocAgent.Api
dotnet user-secrets set "Authentication:GitHub:ClientSecret" "YOUR_CLIENT_SECRET" --project src/DocAgent.Api
```

Note: many GitHub users keep their email private. The API will request the `user:email` scope to read the user's verified emails; that is what makes automatic account linking (AUTH-5) possible.

---

## 3. Check

List the saved keys. The command prints the values too, so run it only where nobody else can see your screen:

```bash
dotnet user-secrets list --project ~/dotnet/src/DocAgent.Api
```

You should see these 4 keys:

```
Authentication:GitHub:ClientId
Authentication:GitHub:ClientSecret
Authentication:Google:ClientId
Authentication:Google:ClientSecret
```

Then tell Claude "credentials are set". Claude will check that the keys exist without printing their values.
