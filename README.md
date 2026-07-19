# MPR Reporting System — Solution Scaffold

This is a hand-built .NET 8 solution scaffold implementing the architecture we discussed:
ASP.NET Core Web API + EF Core (SQL Server) + ClosedXML export + Tesseract/Docnet OCR
pipeline + Hangfire background jobs + JWT auth.

**No .NET SDK was available in the sandbox that produced this**, so none of this has been
compiled or run. Treat it as a structurally correct starting point, not a tested build —
run `dotnet build` locally first and expect to fix the usual first-build issues (package
version drift, minor typos) before it runs cleanly.

## Solution Layout

```
MPRSystem.sln
MPR.Domain/          Entities + enums, no dependencies
MPR.Application/      DTOs, service interfaces, MprCalculationService (the formula engine)
MPR.Infrastructure/   EF Core DbContext, ClosedXML export, PDF/OCR extraction, Email
MPR.Web/              API controllers, Program.cs, appsettings.json
```

## Prerequisites

1. .NET 8 SDK
2. SQL Server (2019+) — connection string in `MPR.Web/appsettings.json`
3. Tesseract trained data (`eng.traineddata`) placed under `App_Data/tessdata` — required
   only if you wire up the OCR cropping logic (see Gaps below)
4. An SMTP account for the email feature (Office 365 / SendGrid / etc.)

## First-time setup

```bash
cd MPRSystem
dotnet restore
dotnet tool install --global dotnet-ef   # if not already installed

# Create the initial migration and database
dotnet ef migrations add InitialCreate --project MPR.Infrastructure --startup-project MPR.Web
dotnet ef database update --project MPR.Infrastructure --startup-project MPR.Web

dotnet run --project MPR.Web
```

Swagger UI will be available at `/swagger` in Development.

## Seeding the first admin user

There's no seed script included yet — the simplest path is a one-off console snippet
(or a `[HttpPost]` you delete afterwards) that hashes a password with
`PasswordHasher<AppUser>` and inserts the first `AppUser` row with `Role = Admin`,
`CanExportOrEmail = true`. After that, use `POST /api/users` (Admin-only) for everyone else.

## What's genuinely complete here

- Full EF Core domain model matching your template's structure (grades, subject
  categories, RC/O2O/PW/TT, Grade 11/12 subject-wise blocks, period week-dates per
  date-family, audit log, email log).
- `MprCalculationService` reimplements your exact formula chain
  (`MAX` per week across subjects → `AVERAGE` across weeks → `ROUNDUP` to whole number),
  verified against the live formulas I read out of your uploaded workbook
  (e.g. `MPR!D13 = MAX(D10:D12)`, `H13 = AVERAGE(D13:G13)`, `I13 = ROUNDUP(H13,0)`).
- `ExcelExportService` rebuilds the `MPR`/`MPR 5WK` sheet block-by-block with the same
  labels, header rows, and live formulas (not hardcoded values) plus the `MPR All`
  summary sheet with the `>70 → Paid` rule as an actual `=IF()` formula.
- Full wizard API surface (steps 1–7), user management (points 9), export/email
  (point 10) with role + `CanExportOrEmail` claim-based authorization, and several of
  the 20+ analytics endpoints (paid-ratio trend, low-attendance students, threshold
  proximity, extraction quality, audit/email logs).

## What is intentionally stubbed — you must finish this before going live

1. **PDF/OCR extraction (`PdfExtractionService`)** — this is no longer a stub. I rendered
   your actual sample PDFs and tested the approach before writing this code:
   - **Grid detection is self-calibrating and validated.** Morphological line extraction
     (`TableGridDetector`) reliably finds each page's row/column boundaries from the
     scan itself — I confirmed this against pages from `L1__L2...L10.pdf` at both sparse
     and dense fill levels, including the inner divider between a student's attendance
     mark and homework mark. No per-template pixel offsets needed.
   - **Plain OCR does not work on the handwritten marks.** I ran Tesseract directly on
     cropped v/A/L/H/N/P cells from your samples and it reliably misread them (e.g. a
     checkmark came back as `'a'` or empty). It reads the printed header/date text fine,
     which is why the service still uses Tesseract there but not for the marks themselves.
   - **Template matching is the right tool given the closed vocabulary.** With only 6
     possible glyphs, inset-cropping each cell (to exclude the grid border lines) and
     using ink-density gives clean blank/non-blank separation (validated: true-blank
     cells measured 0.0 ink ratio vs 0.05–0.4 for marked cells after insetting), and
     `MarkClassifier` then runs OpenCV template matching against a small labeled
     reference library for non-blank cells.
   - **What's still open:** the template library ships empty. It needs an initial
     labeled set (a handful of crops per glyph, per teacher's handwriting style if it
     varies a lot) before match confidence will be high. I built the fix for this
     directly into the wizard rather than requiring an upfront dataset: every time a
     reviewer corrects a low-confidence cell via `POST
     /api/wizard/attendance/{recordId}/correct-mark`, that crop is saved into
     `ITemplateLibrary` under the corrected label (see `ExtractionCellSample` +
     `FileSystemTemplateLibrary`). Accuracy should measurably improve over your first
     few real reports as the library grows — track this with the
     `GET /api/reports/periods/{id}/extraction-quality` endpoint.
   - Strike-through detection (`MarkClassifier.DetectStrikeThrough`) uses a
     bounding-box aspect-ratio heuristic on the inked contour; this is a reasonable
     first pass but wasn't validated against a struck-out cell in your samples (none
     of the pages I rendered happened to show one) — watch its false-positive rate
     early on and tighten `StrikeThroughAspectRatio` if needed.
   - `OpenCvSharp4.runtime.win` is referenced for Windows; swap to the matching
     `OpenCvSharp4.runtime.ubuntu.22.04-x64` (or your distro's) package if deploying
     to Linux.
2. **EF Core migrations** — not generated (no SDK available here). Run
   `dotnet ef migrations add InitialCreate` yourself; the model as coded should produce
   a reasonable schema on the first try.
3. **Seed data** — Grades 1–12 (with `IsSubjectWiseOnly` = true for 11 & 12), Subjects
   per grade (Eng/Math/Reasoning/Science/O2O pairs/Power Writing/Biology/Chemistry/
   FM-MM/GM/English for 11-12), and the first Admin user need a seed script.
4. **Frontend** — no UI is included. The wizard is designed as a clean API surface;
   pair it with Blazor Server, a Razor Pages wizard, or a separate SPA per your team's
   preference. If you want, I can scaffold the frontend next.
5. **Hangfire wiring for extraction** — `ExtractFileAsync` currently runs inline in the
   upload request for scaffold clarity; swap to `BackgroundJob.Enqueue<T>(...)` so large
   batch uploads don't block the HTTP request.
6. **Raw mark retention** — the schema currently collapses v/L → 1 and A/struck-out → 0
   at extraction time. If you want the "Late (L) Frequency Report" (#13 in the analytics
   list) or any report distinguishing *why* a week is 0/1, add a `RawMark` enum column
   to `AttendanceRecord` before this goes to production — cheap to add now, harder to
   backfill later.
7. **Hangfire dashboard security** — `/jobs` is mapped with no policy in `Program.cs`;
   restrict it to Admin before deploying externally.

## Chat Agent (new)

A conversational front-end now sits alongside the wizard API, at `/chat.html` once
the app is running. It's a genuine tool-calling agent, not a scripted flow: the model
decides which MPR operations to call based on what you ask.

### Why Ollama

You asked for a free option. Claude/OpenAI APIs have real per-token costs beyond a
small trial credit; **Ollama** (https://ollama.com) runs an open-source model
(Llama 3.1, Qwen2.5, etc.) entirely on your own server with no per-message cost, and
keeps student attendance data off third-party APIs entirely - worth considering
regardless of budget, given what this data is.

### Setup

```bash
# On the server that will run MPR.Web (or a machine it can reach):
curl -fsSL https://ollama.com/install.sh | sh   # Linux; see ollama.com for Windows/Mac
ollama pull llama3.1                             # ~4.7GB download, needs tool-calling support
ollama serve                                     # runs on http://localhost:11434 by default
```

Point `MPR.Web/appsettings.json` → `Ollama:BaseUrl` at wherever Ollama is running
(default `http://localhost:11434` assumes same machine). `Ollama:Model` must match
whatever you pulled — swap to `qwen2.5` or similar if `llama3.1` is too large for your
hardware (it needs ~8GB RAM to run comfortably; smaller models trade off tool-calling
reliability).

### How it fits together

- **Tools = existing wizard operations.** `MprToolCatalog.cs` defines the callable
  functions (create report period, list uploads, recalculate, get preview, finalize,
  email, etc.); `MprToolExecutor.cs` runs them against the exact same
  `IMprCalculationService`/`IExcelExportService`/`AppDbContext` the wizard REST API
  uses. Nothing is duplicated — the chat agent is a second front-end on the same
  backend, not a separate implementation.
- **PDF upload is NOT a tool call.** LLM tool-calling protocols pass JSON, not binary
  data, so the chat page's 📎 button posts multipart form data straight to the
  existing `/api/wizard/periods/{id}/upload` endpoint, then tells the agent in plain
  text what was uploaded. The agent picks up from there via `list_uploaded_files`.
- **Guardrails are in the system prompt, not enforced in code** — `finalize_report`
  and `email_report` are instructed not to fire without explicit user confirmation
  in the conversation. This is a prompt-level guardrail, not a hard constraint; if you
  need this to be unbreakable (e.g. compliance reasons), add an explicit
  confirmation-token step in `MprToolExecutor` rather than relying on the model
  following instructions.
- **Conversation history persists** in `ChatSessions`/`ChatMessages`, including tool
  call arguments and results, so you can audit exactly what the agent did and why.

### What's unverified here

Like the rest of this scaffold, this was written without a working .NET build or a
running Ollama instance to test against. The specific risk areas:
- Ollama's exact tool-calling JSON shape (`message.tool_calls[].function.arguments`)
  was implemented from its documented API — verify against your installed version,
  since this has changed across Ollama releases.
- No streaming — replies wait for the full model response, which can be slow on
  modest hardware for longer tool-calling chains. Consider streaming responses
  (`stream: true` + SSE to the browser) if response latency is a problem.
- `chat.html` is intentionally minimal (no auth token refresh, no session list UI) —
  functional for testing the agent loop, not a polished product UI.

## Formula reference (from your uploaded template, for your own verification)

```
MPR!D13   =MAX(D10:D12)              " No. Of Std Present, week 1
MPR!H13   =AVERAGE(D13:G13)          " Total across the 4/5 weeks
MPR!I13   =ROUNDUP(H13,0)            " MPR value
MPR!L20   =SUM(L16:L19)              " O2O tutor-pair weekly total
MPR All!E4 =SUM(F4:AS4)              " Row total across RC/O2O/PW per period
```

Paid/Unpaid in the sample workbook is a manually-typed `STATUS` column, not a formula —
this scaffold instead computes it live (`MPRSummary.Status` in code, `=IF(F>70,...)` in
the exported sheet) per your stated rule, so it never drifts out of sync with the numbers.
