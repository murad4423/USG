# Ultrasound Reporting App

A Windows desktop app for a hospital ultrasound department: receives
studies from a GE Voluson E8 (or any DICOM-C-STORE-capable modality),
lists patients/studies, composes and silently prints image sheets, and
generates/prints exam reports from HTML templates — all with full Bengali
(Bangla) text support and zero Microsoft Office/Word dependency.

Stack: C#/.NET 8 (WinForms host + embedded WebView2) driving a React/Vite
frontend, fo-dicom for DICOM, Scriban for report templating, QuestPDF for
image-sheet composition, a headless WebView2 for HTML→PDF report
rendering, SQLite (via Microsoft.Data.Sqlite) for storage, and native
Windows printing (no OS print dialog).

> Looking for the phase-by-phase build history instead of current
> instructions? See [BUILD_LOG.md](BUILD_LOG.md).

## Contents

1. [Prerequisites](#1-prerequisites)
2. [Build](#2-build)
3. [Run (development)](#3-run-development)
4. [Manual DICOM upload — developing without the physical machine](#4-manual-dicom-upload--developing-without-the-physical-machine)
5. [Configuring Settings](#5-configuring-settings)
6. [Producing a distributable build](#6-producing-a-distributable-build)
7. [Verifying the packaged build](#7-verifying-the-packaged-build)
8. [Project layout](#8-project-layout)
9. [Where the app keeps its data](#9-where-the-app-keeps-its-data)
10. [Troubleshooting](#10-troubleshooting)

---

## 1. Prerequisites

- Windows 10/11 (the app is Windows-only — WinForms + WebView2 +
  `System.Drawing.Printing`)
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Node.js](https://nodejs.org/) 18+ (LTS recommended) and npm
- WebView2 Runtime — already preinstalled on most current Windows 10/11
  machines; if missing, get it from
  https://developer.microsoft.com/microsoft-edge/webview2/
- A printer is **not** required for development. If none is installed (or
  the configured one is unreachable), every print/reprint automatically
  falls back to saving the composed PDF under `data/print-fallback/`
  instead of failing.

## 2. Build

### 2.1 Frontend

```bash
cd src/frontend
npm install
npm run build
```

This produces `src/frontend/dist/`. `UltrasoundApp.Host.csproj` copies
`dist/` into its own build output folder (`wwwroot/`) automatically every
time you build the Host project — see the `CopyFrontendBuild` target.
**Run this before building/running the Host for the first time**, and
again after any frontend change, unless you're using `npm run dev` (see
§3).

### 2.2 Backend

From the repository root:

```bash
dotnet build UltrasoundApp.sln
```

Or open `UltrasoundApp.sln` in Visual Studio 2022 and build there.

## 3. Run (development)

**Option A — hot-reload frontend (recommended while iterating on UI):**

```bash
# terminal 1
cd src/frontend
npm run dev
# terminal 2, repo root
dotnet run --project src/UltrasoundApp.Host
```

A **Debug**-configuration build points the embedded WebView2 at Vite's
dev server (`http://localhost:5173/`) instead of the built `wwwroot/`
files, so frontend edits show up on save without rebuilding the host.

**Option B — full build, closer to production:**

```bash
cd src/frontend && npm run build && cd ../..
dotnet run --project src/UltrasoundApp.Host --configuration Release
```

**What you should see:** a single maximized window titled "Ultrasound
Reporting" with a dark left-hand nav (Patient List / Image Print / Report
Print / Settings) and a "Bridge OK" status line at the bottom of the nav
once the React↔C# IPC bridge finishes its startup ping/pong. Patient List
is shown by default.

Run from a terminal (as above) rather than double-clicking the built
`.exe` while developing — `UltrasoundApp.Host` is a `WinExe`, so its
console output (including `AppLog`'s echoed lines and the DICOM
listener's startup/bind messages) only appears when launched from an
attached terminal.

## 4. Manual DICOM upload — developing without the physical machine

You never need real ultrasound hardware to develop or test this app. Two
independent paths get a `.dcm` study into the database, and both run
through the exact same `DicomFileParser` → `DicomProcessingService`
pipeline, so a study imported either way is grouped/deduplicated
identically and shows up the same way in Patient List.

### 4.1 Manual upload button (simplest — always available)

1. Get a sample `.dcm` file. Any valid DICOM file with a Patient ID,
   Study Instance UID, and SOP Instance UID works — it doesn't need to be
   an actual ultrasound image. Options:
   - dcm4che ships several tiny sample files under its own
     `etc/testdata/` folder (https://github.com/dcm4che/dcm4che).
   - Search "sample DICOM file" for public test datasets.
   - `pydicom`'s test data (`pydicom.data.get_testdata_file(...)`) if you
     have Python handy.
2. Run the app (§3) and go to **Patient List**.
3. Click **Manual DICOM Upload**, pick the file. You'll see a one-line
   result: which patient/study/image was created vs. matched to an
   existing one (re-uploading the same file a second time correctly
   reports "already imported — no duplicate created", never a second
   row).
4. To test grouping, upload a second file that shares the first file's
   `StudyInstanceUID` (edit the tag with a tool like `dcm4che`'s
   `dcm2xml`/`xml2dcm`, or `pydicom`, if your sample files don't already
   share one) — Patient List should show one study row with two images
   nested under it, not two rows.

### 4.2 Simulating the auto-receive listener (no machine, no manual click)

The app also runs a background DICOM C-STORE SCP listener
(`DicomScpListener`) from the moment it starts, listening on the AE
Title/port configured in Settings (§5) — `ULTRASOUNDAPP` / `11112` by
default on a fresh install. You can push a test file to it exactly the
way a real ultrasound machine would, from the same or another machine on
the network:

1. **Get a C-STORE SCU tool.** Either works:
   - dcm4che's `storescu` — download from
     https://github.com/dcm4che/dcm4che/releases and put it on your
     `PATH`.
   - fo-dicom's own `DicomClient` class, driven from a few lines of C#,
     if you'd rather not install anything extra.
2. Run the app (§3) — the terminal should log
   `Listening for C-STORE on port 11112 (AE Title 'ULTRASOUNDAPP')`
   during startup.
3. Push a file from another terminal:
   ```bash
   storescu -c ULTRASOUNDAPP@localhost:11112 path\to\test.dcm
   ```
4. Confirm the app's terminal logs a "Received SOP Instance..." line and
   the study appears in **Patient List** — no manual upload click needed.
5. Re-push the same file to confirm no duplicate is created, and push a
   non-DICOM file renamed to `.dcm` to confirm the listener logs a clean
   per-file error and keeps listening rather than crashing.

A standalone console harness (`UltrasoundApp.Dicom.ManualTest`) is also
available if you want to run just the listener without the full UI:
`dotnet run --project src/UltrasoundApp.Dicom.ManualTest`. It points at
the same real `data/app.db` the main app uses.

### 4.3 Getting a report onto a study without hand-typed data

Once a study exists (via either path above), open **Patient List**, click
its row to expand it, then **Print Report** to open the Report Print form
pre-filled with the DICOM-derived Patient Name/ID/Age/Sex/Date. Two exam
types have example report templates seeded automatically on first launch
(`templates/reports/whole-abdomen.html` and
`.../pregnancy-profile.html`), matched against a study's `ExamType` —
`"WholeAbdomen"` and `"PregnancyProfile"` respectively. If your test
study's exam type doesn't match either, add a `ReportTemplate` row by
hand (see §10) pointing at one of those two files, or your own.

## 5. Configuring Settings

Open the **Settings** nav item. Every field takes effect immediately on
**Save** — no app restart required — because each subsystem reads
Settings fresh rather than caching it at startup.

| Section | Fields | Effect |
|---|---|---|
| **DICOM Auto-Receive** | AE Title, Listening Port | The background SCP listener (§4.2) is restarted on the new AE Title/port if you changed either. If the new port is already in use, the save is still kept (so you don't lose your typed values) but you'll see a red warning that auto-receive is currently down — fix the port and save again. |
| **Template Folders** | Report Templates Folder, Image Sheet Branding Template File | Leave blank to use the built-in defaults (`templates/reports/`, `templates/image-sheets/default-branded-template.json`). Override only if you're pointing at a different folder/file. |
| **Printer** | Printer (dropdown of installed Windows printers, or "System default") | Every print/reprint resolves its printer through this setting unless a specific request overrides it. No printers installed → dropdown is just "(System default)"; printing then falls back to `data/print-fallback/` (see §1). |
| **Hospital Branding** | Logo Path, Hospital Name, Address, Header Text, Footer Text | Layered onto every image-sheet print. A field left blank falls back to whatever the static JSON branding template already had — it's never blanked out. |

**Persistence:** all of this is stored in a single-row `Settings` table in
`data/app.db` and survives app restarts.

## 6. Producing a distributable build

The published output is a **self-contained folder** (or, with
`PublishSingleFile`, a single `.exe` plus a small number of native/config
files next to it) that runs on a target Windows machine with no .NET SDK
and no source tree required.

### 6.1 Build the frontend first

Publishing the Host project copies `src/frontend/dist/` into the
published output automatically (see `CopyStaticAssetsToPublishDir` in
`UltrasoundApp.Host.csproj`), but it has to exist first:

```bash
cd src/frontend
npm install
npm run build
cd ../..
```

If `dist/` is missing, `dotnet publish` (next step) fails fast with a
clear error rather than silently producing a build that opens to a blank
window.

### 6.2 Publish

From the repository root, choose one of:

**Single-folder, self-contained (simplest to reason about; a handful of
files, no separate .NET install needed on the target machine):**

```bash
dotnet publish src/UltrasoundApp.Host/UltrasoundApp.Host.csproj ^
  -c Release ^
  -r win-x64 ^
  --self-contained true ^
  -p:PublishSingleFile=false ^
  -o dist/UltrasoundApp
```

**Single-file .exe (fewer files to hand off — the SQLite native library
still extracts itself to a temp folder at startup, so
`IncludeNativeLibrariesForSelfExtract` is required):**

```bash
dotnet publish src/UltrasoundApp.Host/UltrasoundApp.Host.csproj ^
  -c Release ^
  -r win-x64 ^
  --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -o dist/UltrasoundApp
```

(On macOS/Linux building for a Windows target — e.g. cross-publishing
from a CI runner — use `\` line continuations instead of `^`, and drop
the `-r win-x64` only if you intentionally want a framework-dependent
build instead; framework-dependent builds require the .NET 8 Desktop
Runtime to already be installed on the target machine, which is usually
not what you want for a clinic deployment.)

Either command produces, under `dist/UltrasoundApp/`:

- `UltrasoundApp.exe` (plus supporting `.dll`/native files in the
  single-folder case)
- `wwwroot/` — the built React app
- `templates/` — the report HTML templates and image-sheet branding JSON

There is **no `data/` folder yet** — that's correct. Every path helper
(`AppPaths`, `TemplatingPaths`, `PrintingPaths`, `ImageStoragePaths`,
`AppLog`) creates its own `data/` subfolders next to the executable on
first use (fresh SQLite database with schema applied via
`DbInitializer.Initialize`, empty `logs/`, `images-extracted/`, etc.) —
a fresh install with no leftover state is the intended starting point.

### 6.3 Hand off / install

Zip the entire `dist/UltrasoundApp/` folder (not just the `.exe`) and
copy it to the target machine — `wwwroot/` and `templates/` must travel
with the executable. Nothing needs installing beyond the WebView2
Runtime (see §1), which is already present on most current Windows
10/11 machines.

## 7. Verifying the packaged build

On the target machine (or a clean folder on your dev machine, to
simulate one — the point is testing a copy with **no** `UltrasoundApp.sln`
anywhere above it, since that's what forces the fallback path resolution
described in §6.2 to actually be exercised):

1. **Launch:** double-click `UltrasoundApp.exe`. Confirm the window opens
   maximized, the nav shows all four items, and the status line reads
   "Bridge OK" within a second or two. (No terminal is attached here, so
   you won't see console/log lines — that's expected; check
   `data/logs/app-<date>.log` afterward instead if something looks
   wrong.)
2. **Upload → Patient List:** click **Manual DICOM Upload**, pick a
   sample `.dcm` file (§4.1). Confirm the success message and that the
   study appears in the table.
3. **Image print:** expand the study's row, click **Print Images**.
   Confirm the thumbnail grid loads, the print preview composes (or shows
   "No images in this study" if the sample file had none — that's a
   correct empty state, not a bug), and clicking **Print** reports either
   a printer name or a fallback-saved path.
4. **Report print:** from the same row, click **Print Report** for a
   study whose exam type matches a seeded template (§4.3). Confirm the
   form loads with DICOM-derived fields pre-filled, the preview renders,
   and **Print Report** succeeds.
5. **Reprint:** back in Patient List, use **Reprint Images** / **Reprint
   Report** on the same row. Confirm both succeed without needing the
   original DICOM file again, and that **Edit & Reprint** reopens the
   form pre-filled with the previously saved values.
6. **Settings:** open **Settings**, confirm it loads without error,
   change something harmless (e.g. Header Text), save, and confirm the
   green "Saved." message. Re-open Settings (or restart the app) and
   confirm the value persisted.
7. **Confirm standalone-ness:** check that `data/app.db`,
   `data/logs/`, etc. were created directly under the folder holding
   `UltrasoundApp.exe` — not anywhere else — and that the app works
   identically whether or not the machine has internet access, Node.js,
   or the .NET SDK installed (a self-contained publish needs none of
   those at runtime).
8. **Restart persistence:** close the app fully and relaunch. Confirm the
   uploaded study, generated report, and changed Setting are all still
   there — proving state lives in `data/` next to the executable, not in
   memory or a temp folder that gets wiped.

If every step above passes on a machine with no source tree and no SDK
installed, the distributable build is verified.

## 8. Project layout

- `src/UltrasoundApp.Host` — WinForms + WebView2 shell, IPC bridge
  (`WebViewBridge.cs`), entry point (`Program.cs`), packaging targets
  (`UltrasoundApp.Host.csproj`).
- `src/UltrasoundApp.Core` — shared models, services (business logic —
  `DicomProcessingService`, `PatientStudyService`, `ReportRenderService`,
  `SettingsService`), repository interfaces, `AppLog`.
- `src/UltrasoundApp.Data` — SQLite access (`AppDbContext`,
  `DbInitializer`, repositories), `AppPaths`.
- `src/UltrasoundApp.Dicom` — DICOM parsing (`DicomFileParser`), the
  auto-receive listener (`DicomScpListener`), `ImageStoragePaths`.
- `src/UltrasoundApp.Templating` — report HTML templating
  (`TemplateEngine`, Scriban-based) and HTML→PDF rendering
  (`PdfRenderer`, headless WebView2), `TemplatingPaths`.
- `src/UltrasoundApp.Printing` — image-sheet composition
  (`ImageSheetComposer`, QuestPDF), silent printing (`SilentPrinter`),
  `PrintingPaths`.
- `src/UltrasoundApp.*.ManualTest` — standalone console harnesses for
  exercising Dicom/Printing/Templating/ReportRender/Settings in
  isolation, each pointed at the real `data/app.db`.
- `src/frontend` — the React/Vite UI (see `src/frontend/src/pages` and
  `.../components`).
- `templates/reports/`, `templates/image-sheets/` — the HTML report
  templates and the image-sheet branding JSON.
- `data/` — created at runtime (see §9); not checked into source
  control.

## 9. Where the app keeps its data

Every path is resolved the same way: walk up from the running
executable looking for `UltrasoundApp.sln`; if found (a dev checkout),
anchor everything there; if not (a published/standalone build), anchor
everything next to the executable instead. So in development everything
lives under the repo root's `data/`, and in a packaged build it lives
next to `UltrasoundApp.exe`:

- `data/app.db` — the SQLite database (patients, studies, images, report
  templates, generated reports, print job history, settings).
- `data/dicom-incoming/` — raw `.dcm` files received by the auto-listener
  before parsing.
- `data/images-extracted/` — converted images + thumbnails extracted from
  DICOM instances.
- `data/generated-reports/` — rendered report PDFs.
- `data/print-fallback/` — where a print job's PDF is saved if no
  physical printer could be reached.
- `data/print-preview/` — scratch PDFs backing the live print-preview
  panes.
- `data/webview2-templating-cache/` — a dedicated WebView2 user-data
  folder for the hidden headless instance `PdfRenderer` uses (kept
  separate from the visible shell's own WebView2 profile).
- `data/logs/app-YYYY-MM-DD.log` — daily rotating application log
  (30-day retention), written by `AppLog`. This is the first place to
  check after anything goes wrong in a build with no attached console.

## 10. Troubleshooting

- **Window opens but shows blank/white:** almost always a missing
  `wwwroot/` next to the executable — confirm `npm run build` was run in
  `src/frontend` before the last build/publish (§2.1, §6.1).
- **A study's exam type has no report template:** the Report Print form
  shows "Could not load the report template for this exam type." Add a
  matching `ReportTemplate` row — either seed it the way
  `Program.cs`'s `SeedDefaultReportTemplates` does for the two built-in
  exam types, or insert one directly:
  ```sql
  INSERT INTO ReportTemplates (ExamType, HtmlTemplateFilePath, PlaceholderFieldsJson)
  VALUES ('YourExamType', 'C:\full\path\to\templates\reports\your-template.html', '[]');
  ```
- **Auto-receive listener shows "Failed to bind":** the configured port
  is already in use — either by a previous unstopped
  `UltrasoundApp.Dicom.ManualTest` run or another process. Change the
  port in Settings (§5) or free the port and restart the app; a failed
  bind is non-fatal — the rest of the app (including Manual DICOM
  Upload) keeps working regardless.
- **Nothing prints and no fallback PDF appears:** check
  `data/logs/app-<date>.log` for the actual `SilentPrinter` error — most
  commonly an invalid printer name left over in Settings from a machine
  that no longer has that printer installed; clear the Printer field to
  fall back to "(System default)".
- **Want to inspect the IPC traffic directly:** right-click the running
  app window → **Inspect** (or press F12) to open Chromium DevTools for
  the embedded WebView2 and watch/drive `window.chrome.webview` messages
  from the Console tab.
