# Sarab

Sarab is a realtime web party game where everyone receives a secret prompt, writes one word, and tries to spot the answer that came from the alternate prompt.

## What Is Included

- React mobile-first client in `src/Sarab.Client`
- ASP.NET Core API and SignalR hub in `src/Sarab.Api`
- Supabase/Postgres-ready storage for prompt packs, anonymized answer history, and summaries
- Local in-memory fallback with a starter prompt pack
- Admin JSON upload flow
- Deterministic scoring: confidence, self-report timing, copycat/obvious penalties, caught penalties, jackpot rollover
- English default UI with Omani Arabic toggle and RTL support
- Dockerfile and `render.yaml` for a free-tier Render MVP deploy

## Local Development

Run the backend:

```powershell
dotnet run --project src/Sarab.Api/Sarab.Api.csproj --launch-profile http
```

Run the frontend:

```powershell
npm run dev --prefix src/Sarab.Client
```

Open the Vite URL shown in the terminal. If port `5173` is busy, Vite will choose the next free port.

Without a database connection, Sarab uses an in-memory starter pack and stores history only until the backend restarts.

## Supabase/Postgres Configuration

Set these environment variables for production:

```text
ConnectionStrings__SarabDb=postgresql://...
Supabase__JwtIssuer=https://YOUR_PROJECT_REF.supabase.co/auth/v1
Supabase__JwtAudience=authenticated
```

Admin endpoints require a Supabase JWT whose claims include `role=admin` or `admin` inside `app_metadata`.

The React admin sign-in also needs:

```text
VITE_SUPABASE_URL=https://YOUR_PROJECT_REF.supabase.co
VITE_SUPABASE_ANON_KEY=YOUR_SUPABASE_ANON_KEY
```

## Prompt Pack JSON

See `prompts/starter-pack.json`.

Each pack has one language: `en` or `ar-om`. Each category contains authored rounds, and each round has exactly two prompts, a `0-100` closeness score, and optional obvious answer lists.

## Verification

```powershell
dotnet test Sarab.slnx
npm run lint --prefix src/Sarab.Client
npm run build --prefix src/Sarab.Client
```

## Render Deploy

1. Create a Render web service from this repo.
2. Use the included `render.yaml`.
3. Add the Supabase connection/auth environment variables.
4. Deploy.

The Dockerfile builds the React client, copies it into the ASP.NET Core app, and serves API, SignalR, and static files from one service.

## Render Dev-Time Blueprint

The included `render.yaml` is currently tuned for a dev-time public playtest service named `sarab-devtime`.

Render will prompt for secret values marked `sync: false`. For bot playtesting, set:

```text
OpenAI__ApiKey=YOUR_ROTATED_OPENAI_KEY
```

Optional Supabase values can be left unset for a temporary in-memory playtest, but saved packs/history/admin auth will not persist across service restarts without `ConnectionStrings__SarabDb` and Supabase auth settings.

Dev bots are enabled with:

```text
Sarab__DevBots__Enabled=true
Sarab__DevBots__UseLlm=true
Sarab__DevBots__Model=gpt-5.4-mini
```
