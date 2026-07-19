# Deploying to Render (free tier)

## What changed to make this possible
- Database: SQL Server → **PostgreSQL** (Render gives you a free Postgres instance)
- Chat AI: local Ollama → **Groq API** (free tier, hosted — no RAM/GPU needed on Render's side)
- Added a `Dockerfile` (installs Tesseract OCR as part of the image, since Render's
  Docker deploy is the only way to get that native binary onto the free tier)
- `docs/bootstrap-schema.sql` is now stale — it's SQL Server syntax. Ignore it for
  this path; EF Core migrations against Postgres are the way forward here instead.

## Step 1 — Get a free Groq API key
1. Go to https://console.groq.com → sign up (free)
2. Create an API key, copy it — you'll paste it into Render in Step 4

## Step 2 — Push this project to GitHub
Render deploys from a GitHub repo, not a zip upload.

```bash
cd MPRSystem
git init
git add .
git commit -m "Initial MPR system"
```
Create a new empty repo on github.com, then:
```bash
git remote add origin https://github.com/YOUR_USERNAME/mpr-system.git
git branch -M main
git push -u origin main
```

## Step 3 — Create the Render services
1. Go to https://render.com → sign up (free) → **New → Blueprint**
2. Connect your GitHub account, select the `mpr-system` repo
3. Render reads `render.yaml` automatically and proposes:
   - A **Web Service** (`mpr-system`) built from the Dockerfile
   - A **free Postgres database** (`mpr-db`)
4. Click **Apply** — it will start building. First build takes several minutes
   (compiling .NET + installing Tesseract in the Docker image).

## Step 4 — Set the secrets Render can't generate for you
In the Render dashboard, open the `mpr-system` web service → **Environment**, and set:
- `CHATPROVIDER__APIKEY` → the Groq key from Step 1
- `SMTP__USER` / `SMTP__PASSWORD` → only needed if you want email-sending to work; leave blank otherwise, the app will just fail email sends gracefully (logged in EmailLogEntries)

Everything else (`DATABASE_URL`, `JWT__KEY`) is already wired via `render.yaml`.

## Step 5 — Watch it fail, then fix it (realistically)
This has never been built or deployed by me — no .NET SDK, no Docker, no Render
account were available in the environment that produced this code. **Expect the
first deploy to fail**, most likely on one of:
- A NuGet package version that doesn't exist or conflicts (check Render's build logs)
- `OpenCvSharp4.runtime.linux-x64` native library loading issues inside the container
  (OpenCvSharp on Linux can be finicky about `libc`/`libgdiplus` versions — if this
  fails, the error will be a `DllNotFoundException` at runtime, not a build failure)
- Note: you do NOT need to run `dotnet ef migrations` before this works — `DbSeeder`
  calls `Database.EnsureCreatedAsync()`, which builds the schema directly from the
  EF model on first startup with no migration files required. This is fine for
  getting something running, but means schema changes later won't have a migration
  history — switch to real `dotnet ef migrations` once the app is stable and you
  need to evolve the schema without dropping data.

Paste me the actual Render build/runtime log output when it breaks — that's the
fastest way to get this actually running, faster than me guessing further blind.

## Step 6 — Once it's running
Your app will be live at `https://mpr-system.onrender.com` (or whatever Render
names it). Open `/chat.html` there. Log in with `admin` / `ChangeMe!2026` (seeded
automatically on first startup by `DbSeeder`) and change the password immediately.

**Free tier caveat:** Render's free web services spin down after 15 minutes of
inactivity and take ~30-60 seconds to wake back up on the next request. Fine for
testing, not for something you depend on being instantly available.
