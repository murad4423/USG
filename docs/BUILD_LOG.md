# Ultrasound Reporting App — Build Log (phase-by-phase history)

> Archived for reference. For current build/run/deploy instructions, see [README.md](README.md).


This is the base skeleton only: a WinForms shell hosting a full-window
WebView2 control that loads a React (Vite) UI, plus a proved-out ping/pong
IPC bridge. There is no business logic, database, DICOM handling, or
printing yet — those come in later sessions.

## Prerequisites

- Windows 10/11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Node.js](https://nodejs.org/) 18+ (LTS recommended) and npm
- WebView2 Runtime — already preinstalled on most current Windows 10/11
  machines; if missing, get it from
  https://developer.microsoft.com/microsoft-edge/webview2/

## 1. Build the frontend

```bash
cd src/frontend
npm install
npm run build
```

This produces `src/frontend/dist/`, which the Host project copies into its
own output folder (`wwwroot/`) on build.

> For active frontend development you can instead run `npm run dev`
> (starts Vite's dev server on `http://localhost:5173`) and run the Host
> in **Debug** configuration, which points the WebView2 control at that
> dev server for hot reload. Run `npm run build` again before switching
> back to a Release build.

## 2. Build & run the Host (WinForms shell)

From the repository root:

```bash
dotnet build UltrasoundApp.sln
dotnet run --project src/UltrasoundApp.Host
```

Or open `UltrasoundApp.sln` in Visual Studio 2022, set
`UltrasoundApp.Host` as the startup project, and press F5.

## 3. What you should see / how to verify this step

1. A single borderless, maximized window opens titled "Ultrasound
   Reporting".
2. A dark left-hand navigation bar with four links: **Patient List**,
   **Image Print**, **Report Print**, **Settings**.
3. **Patient List** is shown by default, displaying just the heading
   "Patient List".
4. Clicking each nav link swaps the main content to that page's heading
   only (**Image Print**, **Report Print**, **Settings**) — no other
   content, no data, no tables yet. This confirms client-side routing
   works.
5. At the bottom of the left nav, a status line should read **"Bridge
   OK"** shortly after the window opens. This confirms the React →
   C# → React round trip (ping/pong) over the WebView2 message bridge is
   working. If it instead says "Bridge unavailable", the page is likely
   being viewed outside WebView2 (e.g. opened directly in a normal
   browser) rather than through the Host app.

## Project layout

See the repository root for the full solution structure. In short:

- `src/UltrasoundApp.Host` — WinForms + WebView2 shell, IPC bridge, entry
  point.
- `src/UltrasoundApp.Core` — shared models/services (empty for now).
- `src/UltrasoundApp.Dicom`, `.Data`, `.Templating`, `.Printing` — stub
  class libraries for future features, referencing `Core` only.
- `src/frontend` — the React/Vite UI.
- `templates/`, `data/` — empty folders reserved for future features.

## 4. Manual DICOM upload (this step)

The Patient List page now has a **"Manual DICOM Upload"** button. Clicking
it opens a native file-open dialog filtered to `*.dcm`. Selecting a file:

1. Parses it with `DicomFileParser` (already existed).
2. Runs the result through the new `DicomProcessingService`
   (`UltrasoundApp.Core.Services`), which inserts/updates the `Patients`,
   `Studies`, and `Images` tables in `data/app.db` via the repositories in
   `UltrasoundApp.Data`.
3. Shows a one-line success or failure message under the button — e.g.
   *"scan001.dcm: New patient "DOE^JANE" added. New study created
   (Abdomen US). Image imported."*

This is currently the **only** way DICOM data gets into the app — there
is no network auto-receive (SCP) listener yet, and there is still no
patient list table/UI; the button is just enough surface to trigger the
upload and confirm it worked.

### How to verify

1. Run the app (see steps 1–2 above) and go to **Patient List**.
2. Click **Manual DICOM Upload** and pick a real or sample `.dcm` file.
   You should see a green success message naming the patient, study, and
   image, and saying a *new* patient/study were created.
3. Inspect `data/app.db` (e.g. with the `sqlite3` CLI or "DB Browser for
   SQLite"):
   ```sql
   SELECT * FROM Patients;
   SELECT * FROM Studies;
   SELECT * FROM Images;
   ```
   You should see exactly one row in each table for that file, with
   `Studies.PatientID` and `Images.StudyInstanceUID` correctly pointing
   back to the rows you just inserted.
4. **Idempotency check — same file twice:** click **Manual DICOM Upload**
   again and re-select the *same* `.dcm` file. The message should now say
   the patient and study were *matched* (not created) and that "this
   image was already imported — no duplicate created." Re-run the
   `SELECT` queries above — row counts in all three tables should be
   unchanged.
5. **Idempotency check — second image, same study:** if you have another
   `.dcm` file that shares the same Study Instance UID (e.g. a second
   image from the same exam), upload it. The message should say the
   patient and study were *matched* but a *new* image was imported.
   `SELECT * FROM Images WHERE StudyInstanceUID = '...';` should now show
   two rows, while `Patients` and `Studies` still have exactly one row
   each for that study — confirming no duplicate Patient or Study rows
   are ever created for repeated uploads from the same study.
6. **Failure case:** try uploading a non-DICOM file (e.g. rename a `.txt`
   file to `.dcm`, or pick an actual `.txt` file via "All files"). You
   should see a red error message rather than a crash, and no rows should
   be added to any table.

## 5. Patient/study query service (this step)

Added `PatientStudyService` (`UltrasoundApp.Core.Services`) — a read-only
service over the data manual DICOM upload already writes. It produces one
row **per Study** (with its Patient and every Image grouped underneath),
and can list all of them, search by patient name/ID, or filter by a
study-date range.

It's now exposed over the IPC bridge as three new message types, but
**there is no UI for it yet** — no table, no search box, no date picker.
This step is service + IPC only; see below for how to call it directly.

| Request `type`                     | Payload                                      | Reply `type`                              |
|-------------------------------------|-----------------------------------------------|--------------------------------------------|
| `listPatientStudies`                | *(none)*                                      | `listPatientStudiesResult`                  |
| `searchPatientStudies`              | `{ "searchText": "..." }`                     | `searchPatientStudiesResult`                |
| `filterPatientStudiesByDateRange`   | `{ "startDate": "yyyy-MM-dd", "endDate": "yyyy-MM-dd" }` (either may be omitted) | `filterPatientStudiesByDateRangeResult` |

Every reply looks like:
```json
{
  "type": "listPatientStudiesResult",
  "success": true,
  "records": [
    {
      "patientId": "12345",
      "patientName": "DOE^JANE",
      "dateOfBirth": "1980-04-12",
      "age": null,
      "sex": "F",
      "studyInstanceUid": "1.2.3...",
      "examType": "Abdomen US",
      "studyDateTime": "2024-01-15T09:30:00.0000000",
      "status": "Received",
      "images": [
        { "sopInstanceUid": "1.2.3.4...", "filePath": "...", "thumbnailPath": "..." }
      ]
    }
  ]
}
```
or, on failure (e.g. a bad date), `{ "type": "...Result", "success": false, "error": "..." }`.

### How to call these manually (WebView2 DevTools)

1. Run the app (steps 1–2 above). Make sure you've uploaded at least a
   couple of DICOM files first (step 4) — ideally two images from the
   *same* study, so you can confirm grouping, plus one from a different
   patient/study/date, so search and date filtering have something to
   exclude.
2. Right-click anywhere in the app window and choose **Inspect** (or
   press **F12**) to open the Chromium DevTools for the embedded
   WebView2 control. This is a real Chromium DevTools window — Console,
   Network, everything — pointed at the running React app.
3. In the **Console** tab, first set up a listener so replies get
   printed (only needs to be run once per DevTools session):
   ```js
   window.chrome.webview.addEventListener("message", e => console.log(JSON.parse(e.data)));
   ```
4. **List all** — confirms grouping (one row per study, images nested):
   ```js
   window.chrome.webview.postMessage(JSON.stringify({ type: "listPatientStudies" }));
   ```
   Check the logged `records`: if you uploaded two images for the same
   study, that study should appear as a **single** record whose `images`
   array has **two** entries — not two separate records.
5. **Search** — confirms name/ID matching:
   ```js
   window.chrome.webview.postMessage(JSON.stringify({ type: "searchPatientStudies", searchText: "doe" }));
   ```
   Try a substring of a real patient's name, then their exact Patient ID,
   then a string that matches nothing (expect `"records": []`). Search is
   case-insensitive and matches on name *or* ID.
6. **Filter by date range** — confirms date filtering:
   ```js
   window.chrome.webview.postMessage(JSON.stringify({
     type: "filterPatientStudiesByDateRange",
     startDate: "2024-01-01",
     endDate: "2024-01-31"
   }));
   ```
   Adjust the dates to bracket/exclude your real studies' dates and
   confirm the right ones show up or disappear. Both bounds are
   inclusive, and either can be omitted for an open-ended range, e.g.
   `{ type: "filterPatientStudiesByDateRange", startDate: "2024-01-01" }`
   for "on or after Jan 1".
7. **Bad input** — confirms errors are reported instead of crashing:
   ```js
   window.chrome.webview.postMessage(JSON.stringify({ type: "filterPatientStudiesByDateRange", startDate: "not-a-date" }));
   ```
   Expect `{ "type": "filterPatientStudiesByDateRangeResult", "success": false, "error": "'startDate' is not a valid date: \"not-a-date\". Use yyyy-MM-dd." }`.

## 6. Patient list UI (this step)

The Patient List page now has a real table, backed by the query service
from step 5:

- One row per **study** (Name, ID, Exam Type, Date/Time) — the backend
  already groups by study, so a study with two images never shows as two
  rows.
- Click a column header (**Name**, **ID**, **Date/Time**) to sort by it;
  click again to flip ascending/descending.
- Click anywhere on a row to expand it in place and show that study's
  images as thumbnails underneath. Click a thumbnail and it shows a
  "Print options coming soon" note — no print flow is wired up yet, by
  design.
- The **search box** filters by name/ID as you type, and the **date
  range** picker (From/To) narrows to a day or range — both update the
  table live and can be combined.
- A **Refresh** button re-fetches from the database, and the list
  auto-refreshes after every successful DICOM upload.

One small addition outside the pure frontend: thumbnails/images are
stored as absolute file paths on disk, but Chromium (which WebView2 is
built on) blocks `<img>` tags from loading `file://` paths on a page
served over `http://`/`https://` (which is what both the Vite dev server
and the packaged app use). So `MainForm.cs` now maps a second virtual
host, `https://appimages/`, onto `data/images-extracted/` (alongside the
existing `appassets` mapping for the built frontend), and
`WebViewBridge.cs` rewrites each image's on-disk path into an
`https://appimages/...` URL (`imageUrl`/`thumbnailUrl` on the DTO) before
sending it to React. No new IPC message types were added — this only
changes what the existing three query replies contain.

### How to verify

1. Run the app and go to **Patient List**. You should see a table with a
   header row (▶/expand column, Name, ID, Exam Type, Date/Time) and one
   row per study you've already uploaded.
2. **Grouping / no duplicates:** if you previously uploaded two images
   from the same study (step 4's idempotency check), confirm that study
   still shows as a **single row**. Click the row to expand it — you
   should see **both** images as thumbnails nested underneath, not two
   separate rows in the table.
3. **New upload appears automatically:** click **Manual DICOM Upload**
   and pick a `.dcm` file for a new patient/study. Once the success
   message appears, the table should refresh on its own (no manual
   reload) and show the new row without you touching Refresh.
4. **Thumbnails render:** expand a row and confirm the thumbnails show
   actual rendered images, not broken-image icons or "No preview" boxes.
   If you see "No preview", check that `data/images-extracted/<studyUid>/`
   actually contains files for that study — an empty/missing folder means
   image rendering didn't happen, which is a step-4 issue, not this one.
5. **Print placeholder:** click a thumbnail. You should see "Print
   options coming soon" appear under the thumbnails for that study — and
   nothing else happens (no navigation, no dialog).
6. **Sorting:** click the **Name** header — rows should reorder
   alphabetically; click it again to reverse. Repeat for **ID** and
   **Date/Time**. The active column should show a ▲/▼ arrow matching the
   current direction.
7. **Search filters in real time:** type a substring of a patient's name
   into the search box — the table should narrow to matching rows within
   a fraction of a second, no need to press Enter. Clear the box (✕ or
   delete the text) and every row should reappear. Try a Patient ID
   substring too.
8. **Date range narrows the list:** set "From" to a date after all your
   studies — the table should go empty (or show "No patients match the
   current search/filters."). Widen or clear the range and rows should
   reappear. Try picking the exact day of one study for both From and To
   — only that day's studies should remain.
9. **Search + date range together:** with a date range set that includes
   only some of your studies, type a search term that matches a patient
   both inside and outside that range — only the in-range match should
   remain in the table.
10. **Bridge unavailable case:** if you open the built frontend directly
    in a normal browser tab (not through the WinForms host), the table
    should show a "The native bridge isn't available..." error with a
    **Retry** button, rather than crashing or hanging on "Loading
    patients...".

## 7. Image sheet print backend (this step)

Added `UltrasoundApp.Printing`'s actual logic — **backend only**, no UI.
There is still no `ImagePrintPage`, no thumbnail selection grid, and no
print-preview screen; that's the next, dedicated step. This step just
proves out composing images into a printable PDF and sending it to a
printer, so the later UI step has a working backend to call into.

- **`ImageSheetComposer`** (`UltrasoundApp.Printing`) takes a list of
  selected image file paths plus an `ImageSheetBrandingTemplate` and
  produces a single print-ready PDF via QuestPDF. The grid (columns ×
  rows) is computed from the image count — 1 image gets a 1x1 layout, 4
  gets 2x2, 5 and 6 get 3x2, and so on
  (`ImageSheetComposer.CalculateGridDimensions`); there is no single
  hardcoded grid. If more images are selected than the template's
  `maxImagesPerPage`, the rest spill onto additional pages, each gridded
  the same way.
- **`ImageSheetBrandingTemplate`** / **`ImageSheetBrandingTemplateLoader`**
  load hospital branding + layout info (logo path, hospital name, address,
  header/footer text, page size, margins, images-per-page cap) from
  `templates/image-sheets/default-branded-template.json`. A missing or
  malformed template file falls back to a built-in default instead of
  crashing.
- **`SilentPrinter`** sends a generated PDF to a printer with no OS print
  dialog. See `src/UltrasoundApp.Printing/README.md` for exactly how
  "silent" is achieved and where it's genuinely silent vs. best-effort. On
  a dev machine with no printer installed (the expected case here), it
  automatically falls back to saving the PDF under `data/print-fallback/`
  and opening it for manual inspection.

### How to test this

There's a throwaway console app, `UltrasoundApp.Printing.ManualTest`, for
exactly this — no UI needed:

```bash
dotnet build UltrasoundApp.sln
dotnet run --project src/UltrasoundApp.Printing.ManualTest
```

It generates six synthetic sample images (plain colored squares labeled
"IMG 1".."IMG 6" — no real DICOM files needed), then:

1. Calls `ImageSheetComposer` with 1, 4, 5, and 6 of those images, plus a
   14-image case to prove pagination, writing PDFs to a temp folder printed
   at the top of the console output (something like
   `%TEMP%\ultrasound-printing-manual-test\`).
2. Calls `SilentPrinter` on the 6-image sheet and prints what happened.

**What to check:**

1. Open `image-sheet-1.pdf` — one large image filling most of the page,
   with the hospital name/address/header text at the top and the footer
   text + page number at the bottom.
2. Open `image-sheet-4.pdf` — a 2x2 grid.
3. Open `image-sheet-5.pdf` — a 3x2 grid with one empty cell (5 images in
   6 slots).
4. Open `image-sheet-6.pdf` — a 3x2 grid, fully filled.
5. Open `image-sheet-14.pdf` — **two pages**, since the default template
   caps at 12 images per page; page 1 should show a 4x3-ish grid of 12
   images, page 2 the remaining 2.
6. Check the console output for the `SilentPrinter` result:
   - **No printer installed** (typical dev machine/VM): `Mode` should be
     `SavedToDiskFallback`, `Succeeded` should be `true`, and a PDF viewer
     should pop open showing the 6-image sheet copied into
     `data/print-fallback/`.
   - **A real printer is installed**: `Mode` will be
     `SilentlyPrintedViaShellVerb` (no bundled silent-print helper is
     configured yet — see the Printing project's README) and a page
     should come out of that printer with no dialog appearing first. If
     nothing prints and no error is shown, check that a PDF viewer is
     actually registered as the default handler for `.pdf` files and
     supports the "Print To" verb (Adobe Acrobat/Reader does).
7. To exercise the "malformed template" fallback: temporarily rename
   `templates/image-sheets/default-branded-template.json` and re-run — the
   PDFs should still generate, just with the generic built-in defaults
   ("General Hospital", etc.) instead of whatever the file said. Restore
   the file afterwards.

No further functionality is in scope for this step. The Image Print page
UI (thumbnail selection, live preview, printer picker) will be added in a
dedicated follow-up session.

## 8. Report template backend (this step)

Added `UltrasoundApp.Templating`'s actual logic — **backend only**, no UI.
There is still no `ReportRenderService`, no `ReportPrintPage`, and no way
to trigger this from the running app; that's a later, dedicated step. This
step just proves out merging field values into an HTML report template and
converting the result to a PDF (with correct Bangla rendering), so that
later step has a working backend to call into.

- **`TemplateEngine`** merges an HTML template (file path or raw string)
  with a `Dictionary<string, string>` of field values. Placeholder tokens
  look like `{{PATIENT_NAME}}`, `{{LIVER_SIZE}}`, etc. — implemented with
  [Scriban](https://github.com/scriban/scriban) under the hood (see the
  rationale in `UltrasoundApp.Templating.csproj`). A token present in the
  template with no matching dictionary entry renders blank instead of
  throwing or leaking a literal `{{TOKEN}}`; `MissingFields`/`UnusedFields`
  on the result flag any mismatch.
- **`PdfRenderer`** converts merged HTML into a PDF using WebView2's
  headless `PrintToPdfAsync` — no visible window, no OS print dialog. See
  the class doc comment in `PdfRenderer.cs` for why it needs (and owns) a
  dedicated hidden `Form` + background STA thread.
- Two example templates were added under `templates/reports/`:
  `whole-abdomen.html` and `pregnancy-profile.html`, each with a realistic
  set of placeholder tokens for that exam type. Both set
  `font-family: 'Nirmala UI', ...` — the Bengali-capable font Windows
  10/11 ship out of the box — so Bangla field values render correctly with
  no extra font installation.

### How to test this

There's a throwaway console app, `UltrasoundApp.Templating.ManualTest`,
for exactly this — no UI needed:

```bash
dotnet build UltrasoundApp.sln
dotnet run --project src/UltrasoundApp.Templating.ManualTest
```

It merges a sample field dictionary into **both** example templates —
including a Bangla patient name (`রহিমা খাতুন`) and a full Bangla sentence
inside the Whole Abdomen impression field — and renders each to a PDF
under `data/generated-reports/` (path printed at the top of the console
output).

**What to check:**

1. Console output for each template should show:
   - `MissingFields: (none)` and `UnusedFields: (none)` — confirms every
     placeholder in the template had a value, and every value supplied was
     actually used.
   - `PDF render succeeded: True`.
2. Open `data/generated-reports/test-whole-abdomen.pdf`:
   - The patient name field shows correct Bengali script (**রহিমা খাতুন**),
     not boxes/tofu characters, not `?`, not reversed/garbled glyphs.
   - The Impression box shows the full Bangla sentence, correctly wrapped
     across lines if it doesn't fit on one.
   - The static Bangla disclaimer line at the very bottom of the page also
     renders correctly (this text is baked into the template itself, not
     passed as a field — confirms static Bangla in the template source
     works the same as dynamic Bangla passed in as a field value).
   - Layout looks like a real report: blue header band, patient info row,
     findings table, impression box, signature line — not a blank page or
     raw unrendered `{{TOKENS}}`.
3. Open `data/generated-reports/test-pregnancy-profile.pdf` and confirm
   the same for its own field set (LMP/EDD/biometry table/etc.), plus the
   same static Bangla footer line.
4. To see the "missing field" behavior on purpose: temporarily delete one
   line from either dictionary in `Program.cs` (e.g. remove `["CBD"] =
   "..."`) and re-run — the console should list that token under
   `MissingFields`, and the corresponding cell in the PDF should just be
   blank rather than showing `{{CBD}}` literally or crashing. Restore the
   line afterwards.
5. First run will be slightly slower ("Starting headless WebView2
   host...") while Chromium spins up a new profile under
   `data/webview2-templating-cache/`; subsequent runs reuse it and start
   faster.

## 9. Report generation backend — ReportRenderService (this step)

Added `ReportRenderService` in `UltrasoundApp.Core` — **backend only**, no
UI. There is still no `ReportPrintPage` or `ReportForm`, and no way to
trigger this from the running app; that's the next step. This step wires
together everything the previous steps built (Patient/Study data,
`TemplateEngine`, `PdfRenderer`) into one call that a future UI can use.

- **`ReportRenderService.GenerateReportAsync(studyInstanceUid,
  manualFieldValues, outputPdfPath)`**:
  1. Looks up the `Study` and its owning `Patient`.
  2. Auto-fills `PATIENT_NAME`, `PATIENT_ID`, `AGE`, `SEX`, `DATE` from
     those rows (age from the stored `Age`, falling back to a
     year-difference calculation from `DateOfBirth`).
  3. Layers the caller-supplied exam-specific fields (e.g. `LIVER_SIZE`,
     `IMPRESSION`) on top — manual fields win if a key collides with an
     auto-filled one.
  4. Merges into the exam type's template (`TemplateEngine`) and renders
     to PDF (`PdfRenderer`).
  5. Saves the filled field values (as JSON) and the PDF path into a
     `GeneratedReport` row — **updates** the existing row if the study
     already has one, never creates a duplicate.
- **Architecture note:** `UltrasoundApp.Core` is not allowed to reference
  `UltrasoundApp.Templating` or `UltrasoundApp.Data` (only the other way
  around — see every other project's `.csproj`). So two new interfaces
  were added to Core: `IReportTemplateEngine` and `IReportPdfRenderer`
  (in `UltrasoundApp.Core/Reporting/`). `TemplateEngine` and `PdfRenderer`
  now implement these (via explicit interface implementation — their
  existing public methods are untouched), the same way `PatientRepository`
  already implements Core's `IPatientRepository`. `IReportTemplateRepository`
  and `IGeneratedReportRepository` were likewise moved from
  `UltrasoundApp.Data` into `UltrasoundApp.Core/Repositories/`, matching
  `IPatientRepository`/`IStudyRepository`/`IImageRepository`.

### How to test this

A throwaway console app, `UltrasoundApp.ReportRender.ManualTest`, exercises
the full pipeline against its **own** SQLite database
(`data/manual-test-report-render.db`) — it never touches the real
`data/app.db`:

```bash
dotnet build UltrasoundApp.sln
dotnet run --project src/UltrasoundApp.ReportRender.ManualTest
```

It seeds one test Patient (Bangla name), one test Study (exam type
`WholeAbdomen`), and a `ReportTemplate` row pointing at
`templates/reports/whole-abdomen.html`, then calls
`GenerateReportAsync` **twice** — once to create the report, once more
with a slightly edited `IMPRESSION` field to prove the second call
**updates** the existing `GeneratedReport` row instead of duplicating it.

**What to check:**

1. Console output for the first run should show `ReportRowWasCreated:
   True`; the second run should show `ReportRowWasCreated: False`.
2. Both runs should report `MissingFields: (none)` and `UnusedFields:
   (none)`.
3. `All GeneratedReport rows currently in the test database: 1` — confirms
   no duplicate row was created.
4. Open the PDF at
   `data/generated-reports/test-report-render-service.pdf`:
   - Patient name **রহিমা খাতুন** (auto-filled from the DB, not typed
     manually) renders as correct Bengali script.
   - `AGE` shows `34 Y`, `SEX` shows `Female`, `DATE` shows today's date —
     all auto-filled.
   - The Impression box shows the Bangla sentence with `(revised)`
     appended at the end (proves the second call's field values are the
     ones that actually got rendered/saved).
5. To confirm the update didn't just happen in memory: re-run the harness
   a third time without changing anything — `ReportRowWasCreated` should
   still be `False` and the row count should still be `1`.
6. Delete `data/manual-test-report-render.db` at any point to start the
   test data over from scratch on the next run (it's a throwaway test
   database, safe to delete).


No further functionality is in scope for this step. Wiring this up to a
real `Study`/`ReportTemplate` (via `ReportRenderService`) and a
`ReportPrintPage` UI (template picker, field entry form, preview, print)
will be added in a dedicated follow-up session.

## 7. Report Print UI (this step)

The Patient List's expanded study row now has a second button, **Print
Report**, next to **Print Images** (shown even for a study with zero
images, since a report doesn't need any). Clicking it navigates to the
**Report Print** page with that study loaded.

The Report Print page has three parts:

1. **Auto-filled from DICOM** — Patient Name / Patient ID / Age / Sex /
   Exam Date, pre-populated from the study record. These are editable
   (to correct a mistake) but default to the same values
   `ReportRenderService` would auto-fill anyway — leave them alone and
   the server-side auto-fill is used; only an edited field is sent as an
   override.
2. **Exam Findings** — one input (or, for long free-text fields like
   `IMPRESSION`, a textarea) per exam-specific placeholder the exam
   type's HTML template actually contains. This list comes from a new
   `getReportFormFields` IPC call, which introspects the template the
   same way `TemplateEngine.Merge` already does internally — nothing
   here is hardcoded per exam type.
3. **Preview** — a live, debounced `<iframe>` preview of the merged PDF,
   rendered by calling `ReportRenderService.GenerateReportAsync` itself
   (via a new `composeReportPreview` IPC call) into a scratch file, the
   same pattern `ImagePrintPage` uses for its preview.

Clicking **Print Report** re-generates the report one more time (via a
new `printReport` IPC call) into a stable, per-study path under
`data/generated-reports/`, then hands it to the same `SilentPrinter`
instance the Image Print step already uses.

No report-generation logic (auto-fill, template merge, PDF render,
`GeneratedReport` save) was duplicated in the UI or the bridge — every
new IPC handler in `WebViewBridge.cs` calls straight into the existing
`ReportRenderService`.

### Two example report templates are seeded automatically

`Program.cs` now seeds two `ReportTemplate` rows into `data/app.db` on
every launch (idempotent — skipped if the row already exists):

| ExamType           | Template file                          |
|---------------------|-----------------------------------------|
| `WholeAbdomen`       | `templates/reports/whole-abdomen.html`  |
| `PregnancyProfile`   | `templates/reports/pregnancy-profile.html` |

**A real DICOM file's `StudyDescription` is free text** (e.g. "Abdomen
US"), so it very likely won't match either seeded `ExamType` exactly —
`ReportTemplateRepository.GetByExamType` is an exact match. If Report
Print shows *"No report template is configured for exam type '...'"*,
either:

- Edit the test study's `ExamType` in the database to exactly
  `WholeAbdomen` or `PregnancyProfile`:
  ```sql
  UPDATE Studies SET ExamType = 'WholeAbdomen' WHERE StudyInstanceUID = '...';
  ```
- Or add a `ReportTemplate` row that matches your test file's real exam
  type instead:
  ```sql
  INSERT INTO ReportTemplates (ExamType, HtmlTemplateFilePath, PlaceholderFieldsJson)
  VALUES ('Abdomen US', 'C:\path\to\repo\templates\reports\whole-abdomen.html', '[]');
  ```

### How to test this

1. Run the app (steps 1–2 above) and go to **Patient List**.
2. Upload a test DICOM (step 4) if you haven't already, and note its
   **Exam Type** column value.
3. Make sure a matching `ReportTemplate` row exists for that exact exam
   type — see above. (`WholeAbdomen` has more fields to exercise, so
   it's the better one to test with if you're free to relabel your test
   study.)
4. Expand that study's row and click **Print Report**.
5. **Auto-fill check:** confirm Patient Name / Patient ID / Age / Sex /
   Exam Date are already filled in and match the Patient List row —
   these came straight from the stored DICOM-derived record, not typed
   in.
6. **Manual field entry check:** type into a few Exam Findings fields
   (e.g. Liver Size, Gallbladder). Confirm the inputs accept typing
   normally and multi-line fields like Impression show a resizable
   textarea.
7. **Bangla test string:** type or paste a Bangla sentence into
   **Impression**, e.g. `স্বাভাবিক পেটের আল্ট্রাসনোগ্রাফি রিপোর্ট।`
   Wait ~½s for the debounced preview to refresh and confirm the Bangla
   text renders correctly (not boxes/mojibake) inside the impression box
   in the preview pane — this proves the same WebView2/Nirmala UI text
   shaping the manual test harness verified is working end-to-end
   through the UI too.
8. **Preview correctness:** confirm the preview shows the correct
   hospital header for the exam type, all your typed values in the right
   table cells, and — if you left some fields blank — that the "X fields
   left blank" note above the preview lists exactly the ones you skipped.
9. **Auto-field override check:** edit the Patient Name field to
   something different, wait for the preview to refresh, and confirm the
   preview now shows your edited name instead of the original.
10. Click **Print Report**:
    - **With a printer installed:** confirm a print job is sent (check
      your OS print queue/spooler) and the page shows a green success
      message.
    - **With no printer installed** (typical on a dev machine): confirm
      the page still shows a **green** success message mentioning the
      PDF was saved to disk instead, and that the file exists under
      `data/print-fallback/` and opened automatically in your default
      PDF viewer.
11. **Persistence check:** inspect `data/app.db`'s `GeneratedReports`
    table — there should be exactly one row for this study (`SELECT *
    FROM GeneratedReports WHERE StudyInstanceUID = '...';`), and its
    `FieldValuesJson` should contain the values you typed. Navigate away
    to Patient List and back into Report Print for the same study — the
    fields you filled in (and any auto-field override) should still be
    there, restored from that saved row.
12. **Unconfigured exam type check:** pick (or upload) a study whose
    exam type has no matching `ReportTemplate` row and click Print
    Report — confirm you get a clear red error message naming the exam
    type, instead of a crash or a blank page.

## 10. Reprint from stored record (this step)

Every expanded Patient List row now has a second action group below the
original **Print Images** / **Print Report** buttons:

- **Reprint Images** — shown whenever the study has images. Reprints the
  study's already-extracted images (from `data/images-extracted/`)
  immediately, with no page navigation and no re-selecting anything.
- **Reprint Report** — shown only when `hasGeneratedReport` is true for
  that study (i.e. a `GeneratedReport` row already exists). Reprints the
  study's saved report immediately, reusing its saved field values as-is.
- **Edit & Reprint** — shown alongside **Reprint Report**. Opens the
  Report Print form (same component as **Print Report**, pre-filled from
  the saved values the same way it always has been) so the operator can
  change something before the reprint is generated; its Print button
  reads **Reprint Report** in this mode and calls the reprint IPC instead
  of the normal generate one.

**None of these three actions ever reads the original DICOM file again.**
They work from three kinds of already-persisted local data only:

- `Images` rows (`FilePath`/`ThumbnailPath` on disk under
  `data/images-extracted/`), looked up via a new
  `PatientStudyService.GetByStudy` — added for this step so a reprint can
  fetch one study's images without the full list query.
- The `GeneratedReport` row's saved `FieldValuesJson`, and — when the
  file is still present — its `GeneratedPdfPath` PDF, reprinted byte-for-
  byte with no template merge/render step at all.
- If that saved PDF is missing (or the operator used **Edit & Reprint**
  and changed something), the report is rebuilt via the existing
  `ReportRenderService`, which itself only ever reads `Patient`/`Study`/
  `ReportTemplate` rows and the exam type's HTML template file from disk
  — never DICOM.

### What's new under the hood

- **`IPrintJobRepository`** (moved from `UltrasoundApp.Data` into
  `UltrasoundApp.Core.Repositories`, the same way `IGeneratedReportRepository`
  was in an earlier step) is now actually wired into the app (`Program.cs`
  → `MainForm` → `WebViewBridge`) and used for the first time. Every
  reprint appends one `PrintJobs` row via its `Add` method — the
  repository has no `Update`/`Delete`, so a reprint can never overwrite an
  earlier row.
- Two new IPC messages, handled in `WebViewBridge.cs`:
  - `reprintImages` → `{studyInstanceUid, printerName?}`
  - `reprintReport` → `{studyInstanceUid, manualFieldValues?, printerName?}`
    (omit `manualFieldValues` for the fast "reprint exactly as saved"
    path; include it — as **Edit & Reprint** does — to rebuild from
    edited values first)
- `PatientStudyRecordDto`/`PatientStudyRecord` (the shape `listPatientStudies`
  etc. already returned) gained one new field, `hasGeneratedReport`, so
  the frontend knows whether to offer **Reprint Report** at all.

### How to test this — including proving no DICOM re-receive is needed

1. Run the app, upload a test DICOM (step 4), and use **Print Report**
   on that study to generate a report (step 9's flow) so it has both
   images and a `GeneratedReport` row.
2. **Confirm the actions appear:** go to **Patient List**, expand that
   row. You should see **Reprint Images**, **Reprint Report**, and
   **Edit & Reprint** below the original buttons. Collapse/expand a
   study that has never had a report generated — confirm only
   **Reprint Images** shows (no **Reprint Report**/**Edit & Reprint**).
3. **Prove no DICOM re-receive — the actual verification the task asks for:**
   - Close the app entirely.
   - Move (don't delete) `data/dicom-incoming/` — or wherever your
     original `.dcm` test file lives — somewhere outside the repo, so
     it's provably unavailable to the running app.
   - Restart the app (`dotnet run` in `UltrasoundApp.Host`, or the built
     executable). This is a fresh process — nothing is cached in memory
     from the earlier session; the app only has whatever's in
     `data/app.db` and `data/images-extracted/`/`data/generated-reports/`
     on disk.
   - Go to **Patient List**, find the same study (it should still list
     normally — the list only ever reads the database), expand it, and
     click **Reprint Images**. Confirm it succeeds (green message) even
     though the original `.dcm` file is no longer reachable.
   - Click **Reprint Report**. Confirm it also succeeds.
   - Click **Edit & Reprint**, change one Exam Finding, click the
     **Reprint Report** button on that page. Confirm it succeeds too —
     this path goes through `ReportRenderService`, which (as in step 9)
     never touches DICOM, only the DB rows and the HTML template file.
4. **PrintJobs log check:** with `data/app.db` (e.g. via `sqlite3` or DB
   Browser for SQLite), run:
   ```sql
   SELECT * FROM PrintJobs WHERE StudyInstanceUID = '...' ORDER BY Timestamp;
   ```
   Confirm one row per reprint you triggered above (`Type` = `ImageSheet`
   or `Report`, a `Timestamp`, and a `PrinterUsed` value — empty string is
   expected on a machine with no printer installed, since `SilentPrinter`
   still reports the fallback as a completed print). Confirm the row
   count only ever grows — reprinting the same study again adds another
   row rather than changing an existing one.
5. **Original data untouched check:** re-run the `GeneratedReports` query
   from step 9 (`SELECT * FROM GeneratedReports WHERE StudyInstanceUID =
   '...';`) — still exactly one row. A "reprint exactly as saved" click
   should not have changed `FieldValuesJson`/`GeneratedPdfPath`/
   `ModifiedAt` at all; only the **Edit & Reprint** path (which calls
   `ReportRenderService.GenerateReportAsync` to rebuild the PDF) updates
   that row, the same way **Print Report** always has.
6. **Missing saved PDF fallback check:** delete the file at the study's
   `GeneratedReport.GeneratedPdfPath` from disk (leave the DB row alone),
   then click **Reprint Report** again. Confirm it still succeeds — this
   exercises the fallback that rebuilds from the saved `FieldValuesJson`
   instead of failing outright.
7. **No printer installed (typical on a dev machine):** confirm every
   reprint above still shows a **green** success message mentioning the
   PDF was saved to disk instead, the same fallback behavior as the
   original Print Images/Print Report actions.

## 11. Auto-receive DICOM SCP listener (this step)

`UltrasoundApp.Dicom` now has a `DicomScpListener` class: a persistent
background C-STORE SCP (a "DICOM listener") built on fo-dicom. When a
study is pushed to it over the network, it runs it through the exact same
`DicomFileParser` → `DicomProcessingService` pipeline the **Manual DICOM
Upload** button uses (step 4) — there is one extraction/storage code
path, not two, so a study received this way is grouped and deduplicated
identically to a manual upload.

**This step does NOT start the listener automatically.** `Program.cs`/
`MainForm` don't construct or call it — deciding when the app should
listen (always, on a Settings toggle, etc.) is a later step. For now the
only way to run it is the new `UltrasoundApp.Dicom.ManualTest` console
project.

- **AE Title / port** are hardcoded defaults for this step (a Settings
  screen to make them configurable comes later): AE Title
  `ULTRASOUNDAPP`, port `11112` (the conventional DICOM test-tooling
  port — the standard port 104 needs administrator/root privileges to
  bind on Windows/Linux, which would make this fail by default on a dev
  machine).
- **Bind failures are handled inside the class itself**: if the port is
  already in use (including by a previous, un-stopped run of the
  harness), `Start()` logs a clear error and returns `false` — it never
  throws out of `Start()` or crashes the process.

### How to test this — including without a physical ultrasound machine

You don't need real ultrasound hardware for any of this: any valid
`.dcm` file works as the "test push," including the same sample file you
already used to test **Manual DICOM Upload** in step 4. If you need a
fresh one, dcm4che ships several tiny sample files under its own
`etc/testdata/` folder, and public sample DICOM datasets (search "sample
DICOM ultrasound file") work too — nothing about this listener cares who
produced the file, only that it's valid DICOM with a Patient ID, Study
Instance UID, and SOP Instance UID.

1. **Get a C-STORE SCU tool.** The task suggests dcm4che's `storescu`;
   it's the most common choice:
   - Download dcm4che from https://github.com/dcm4che/dcm4che/releases
     (or `apt install dcm4che2-tools` on some Linux distros, though the
     modern dcm4che 5 release's `storescu` script is more reliable) and
     make sure `storescu` is on your `PATH`.
   - Alternative: fo-dicom itself ships a `DicomClient` class you can
     drive from a two-line C# script/harness if you'd rather not install
     anything extra — construct one pointed at
     `localhost`/`11112`/`ULTRASOUNDAPP` and call `AddRequestAsync(new
     DicomCStoreRequest(path))` then `SendAsync()`.
2. **Start the listener:**
   ```bash
   dotnet run --project src/UltrasoundApp.Dicom.ManualTest
   ```
   You should see it print the database path (the **real** `data/app.db`
   — this harness deliberately does *not* use an isolated test database,
   so what it receives shows up in the actual app's Patient List, letting
   you compare it directly against a manual upload) and then "Listening
   for C-STORE on port 11112 (AE Title 'ULTRASOUNDAPP')."
3. **Push a test file** from another terminal:
   ```bash
   storescu -c ULTRASOUNDAPP@localhost:11112 path\to\test.dcm
   ```
   In the harness console, confirm you see "Association accepted..."
   followed by a "Received SOP Instance '...' -> Study '...' ... Patient
   created, Study created, Image added." line (or "existing"/"already
   present" if you're re-pushing a file used before).
4. **Confirm it's identical to a manual upload:**
   - With the main app also running (`dotnet run --project
     src/UltrasoundApp.Host` in a third terminal), open **Patient List**
     and confirm the pushed study now appears there — same as if you'd
     used **Manual DICOM Upload** with that file.
   - Or inspect `data/app.db` directly (as in step 4):
     ```sql
     SELECT * FROM Patients;
     SELECT * FROM Studies;
     SELECT * FROM Images;
     ```
5. **No-duplicates check — push the exact same file twice:** run the same
   `storescu` command again. The harness console should say the patient
   and study were matched (not created) and the image was "already
   present — skipped, no duplicate." Row counts in all three tables
   should be unchanged from step 4.
6. **Grouping check — a second image from the same study:** if you have
   another `.dcm` file sharing the same Study Instance UID, push it too.
   The console should say patient/study matched but a new image was
   added; `SELECT * FROM Images WHERE StudyInstanceUID = '...';` should
   show two rows while `Patients`/`Studies` still have exactly one row
   each — the same grouping behavior step 4 verified for manual uploads.
7. **Bind-failure check:** with the first harness instance still running,
   open a second terminal and run
   `dotnet run --project src/UltrasoundApp.Dicom.ManualTest` again (same
   default port). Confirm the second instance prints a clear "Failed to
   bind the DICOM listener to port 11112: ..." error and exits cleanly —
   it should not crash or hang.
8. **Bad-file check:** push a non-DICOM file (rename a `.txt` file to
   `.dcm`). Confirm the harness logs a clear "Failed to process incoming
   SOP Instance..." error for that one instance and keeps listening
   (press Enter to stop it manually when you're done) — a single bad file
   never takes down the association or the listener.
9. **Stop it:** press Enter in the harness console (or Ctrl+C). Confirm
   the port is freed — starting the harness again immediately afterward
   should succeed.

## 12. Auto-start the DICOM listener (this step)

`Program.cs` now constructs a `DicomScpListener` — wired to the exact
same `dicomFileParser`/`dicomProcessingService` instances everything else
(Manual DICOM Upload, the step-11 harness) uses — and calls `Start()`
before the main window opens. No button, no console harness, no extra
step: the app listens for C-STORE on `ULTRASOUNDAPP`/`11112` (the same
hardcoded defaults from step 11) from the moment it launches until it
exits, when `Application.ApplicationExit` disposes it alongside
`pdfRenderer`.

**A failed `Start()` is deliberately non-fatal.** `DicomScpListener.Start()`
already catches every failure internally (see step 11) and just returns
`false` — `Program.cs` doesn't even inspect that return value, because
there's nothing conditional to do with it: the rest of `Main()` (Patient
List, Manual DICOM Upload, Print, Report generation — none of which
depend on the listener) runs exactly the same whether or not the
listener came up. The only user-visible trace of a bind failure is the
"Failed to bind the DICOM listener to port 11112: ..." line
`DicomScpListener` itself logs to the console.

Still no Settings screen (a later step) — the AE Title/port stay
hardcoded, and there's no retry/backoff if the port was busy at launch;
if it fails to bind at startup it simply stays not-running for the rest
of that session (restart the app to try again once the port is free).

### How to test this

**Note:** since `UltrasoundApp.Host` is a `WinExe`, its console output
only appears when it's launched from a terminal that stays attached
(exactly how these docs have had you run it throughout —
`dotnet run --project src/UltrasoundApp.Host`); double-clicking the
built `.exe` shows no console window at all. That's an existing
limitation of Console-based logging, not something new here — worth
keeping in mind while testing.

1. **Happy path — starts with no manual trigger:**
   - Make sure nothing else is already bound to port 11112 (stop the
     step-11 harness if it's still running).
   - Launch the app: `dotnet run --project src/UltrasoundApp.Host`.
   - In that same terminal, before touching anything in the UI, confirm
     you see `[DicomScpListener] Listening for C-STORE on port 11112
     (AE Title 'ULTRASOUNDAPP').` — it appears during startup, not after
     any click.
   - From another terminal, push a test file straight away:
     ```bash
     storescu -c ULTRASOUNDAPP@localhost:11112 path\to\test.dcm
     ```
     No manual-upload click, no harness, no other step. Confirm the app's
     terminal logs `Received SOP Instance '...' -> ... Patient created,
     Study created, Image added.` and the study shows up in **Patient
     List** — identical to step 11's result, just without having to run
     the separate harness first.
2. **Simulated bind failure — confirm the rest of the app still works:**
   - Occupy port 11112 *before* launching the app, using the step-11
     harness itself as the "something else already listening" process:
     ```bash
     dotnet run --project src/UltrasoundApp.Dicom.ManualTest
     ```
     Leave it running.
   - In another terminal, launch the real app:
     `dotnet run --project src/UltrasoundApp.Host`.
   - Confirm the app's terminal logs `[DicomScpListener] ERROR: Failed to
     bind the DICOM listener to port 11112: ...` during startup, **and**
     the main window still opens normally — no crash, no error dialog,
     no delay beyond the ~300ms bind-check.
   - Confirm the rest of the app genuinely still works with the listener
     down: use **Manual DICOM Upload** to import a `.dcm` file (step 4)
     and confirm it's processed and appears in **Patient List** exactly
     as before — manual upload never touched `DicomScpListener`, so
     there's nothing for its failed bind to have broken.
   - Stop the harness (frees the port), then close and relaunch the app —
     confirm it now logs the "Listening for C-STORE..." success line
     instead, proving the earlier failure was specific to the occupied
     port, not a bug that persists once it's free.

## 13. Settings persistence (this step)

A new `Settings` table (one row, `Id = 1`, enforced by a `CHECK (Id = 1)`
constraint) holds app-wide configuration: `AeTitle`, `ListeningPort`,
`ReportTemplatesFolderPath`, `ImageSheetBrandingTemplatePath`,
`PrinterName`, `LogoPath`, `HospitalName`, `Address`, `HeaderText`,
`FooterText`. `SettingsRepository` (`UltrasoundApp.Data`) reads/writes it
via a single `Get()`/`Save()` pair — `Save` is one `INSERT OR REPLACE`
against the fixed `Id = 1` key, so there's no separate Add/Update or
"does a row exist yet" check. `SettingsService` (`UltrasoundApp.Core.Services`)
wraps that repository and guarantees `Get()` never returns null: if
nothing has ever been saved, it hands back a fresh `AppSettings` with
built-in defaults (AE Title/port matching `DicomScpListener`'s own
hardcoded defaults; placeholder hospital branding text; folder-path
overrides left `null`, meaning "use whatever built-in default the code
already uses").

**This is persistence only.** Nothing reads these values back out yet:
`DicomScpListener` still uses its own hardcoded AE Title/port,
`ImageSheetComposer`/`SilentPrinter` still use their own defaults, and
report templates still resolve via `TemplatingPaths`'s fixed folder —
none of them know `SettingsService` exists. There's also no
`SettingsPage.tsx`, no IPC message, and `Program.cs`/`MainForm` don't
construct a `SettingsService` at all — both the UI and the actual wiring
into the listener/printer/composer are later steps.

### How to test this

The new `UltrasoundApp.Settings.ManualTest` console harness is the only
way to exercise this for now (there's no button or menu yet). Like
`UltrasoundApp.Dicom.ManualTest`, it deliberately points at the **real**
`data/app.db` rather than a throwaway test database, since whatever you
save here is exactly what a later step's Settings UI (and the app itself,
once it's wired up) will read back.

1. **Save a set of values:**
   ```bash
   dotnet run --project src/UltrasoundApp.Settings.ManualTest -- save
   ```
   Confirm it prints the database path and then echoes back the ten
   values it just saved (AE Title `TESTAE`, port `11113`, etc. — see
   `Program.cs`'s `Save()` for the exact fixed test set).
2. **Reload them — including after an app restart:** run the harness
   again, in a **separate process** (a fresh `dotnet run`, which is
   already a distinct process from step 1's — for an even stronger test,
   reboot the machine between the two):
   ```bash
   dotnet run --project src/UltrasoundApp.Settings.ManualTest -- get
   ```
   Confirm every field printed matches exactly what step 1 saved,
   character-for-character — not the built-in defaults. This is the
   direct "save, then reload, including after restarting" check the task
   asked for: `SettingsService`/`SettingsRepository` hold no in-memory
   state between runs, so the only way "get" in a new process can produce
   the saved values is by actually reading them back from
   `data/app.db`.
3. **Confirm the defaulting behavior on a fresh database:** rename or
   delete `data/app.db` (back it up first if you want to keep your real
   patient data — this deletes everything, not just Settings), then run
   `... -- get` again. Confirm it prints `SettingsService`'s built-in
   defaults (AE Title `ULTRASOUNDAPP`, port `11112`, hospital name
   `General Hospital`, the folder-path fields as `(null)`, etc.) rather
   than an error or leftover values from step 1 — proving `Get()` never
   returns null even on a database that's never had a settings row saved.
4. **Confirm it's a true single-row upsert, not an ever-growing log:**
   after step 1, inspect the table directly:
   ```sql
   SELECT COUNT(*) FROM Settings;   -- should be exactly 1
   SELECT * FROM Settings;
   ```
   Run `... -- save` a second time (it saves the same fixed test values
   again), then re-run the `COUNT(*)` query — still exactly 1, confirming
   `Save` replaces the single row rather than appending a new one each
   time.
5. **Confirm nothing else changed:** with the app not running, spot-check
   that `data/app.db`'s other tables (`Patients`, `Studies`, `PrintJobs`,
   etc.) and the actual running behavior of `DicomScpListener`/silent
   printing are unaffected — this step only adds a new table and two new
   classes nothing else calls yet.

## 14. Settings UI + wiring (this step)

The Settings nav item is now a real form (`SettingsPage.tsx`) instead of
an empty placeholder, and — the important half of this step — the values
it saves are actually *read* by the listener, the image-sheet composer,
and the printer, replacing the hardcoded defaults each of them used
before.

**The form** covers AE Title, listening port, report-templates folder,
image-sheet branding template file, printer (a dropdown populated from
Windows' installed printers), and the branding fields (logo path,
hospital name, address, header text, footer text). It loads on mount and
saves with a single **Save** button.

**Three new IPC messages** back it, all in `WebViewBridge`:
`getSettings`, `saveSettings`, and `listPrinters`.

**How each setting reaches the thing it configures:**

- **AE Title / port → `DicomScpListener`.** `Program.cs` already reads
  these from `SettingsService` at launch (step 13's defaults apply on a
  fresh database). On top of that, `HandleSaveSettings` compares the
  submitted AE Title/port against what the listener is *currently*
  running with and calls `DicomScpListener.Restart(...)` only if they
  actually differ — so a change takes effect immediately, with no app
  restart. A failed rebind (e.g. the new port is already in use) does
  **not** undo the save; it comes back as `listenerRestarted: true` with
  `listenerRunning: false`, and the Settings screen shows a red warning
  saying auto-receive is currently down.
- **Branding → `ImageSheetComposer`.** Every image-sheet compose now goes
  through `CurrentImageSheetBranding()`, which is
  `ImageSheetBrandingTemplate.ApplySettings(SettingsService.Get())` — the
  static JSON template still supplies the page-layout fields Settings
  doesn't cover (page size, margins, cell spacing, max images per page,
  captions), with Settings' branding fields layered on top. Any Settings
  branding field left blank falls back to the JSON template's own value
  rather than blanking it out. Settings are read fresh on every compose,
  so a branding edit shows up on the very next preview/print.
- **Printer → `SilentPrinter`.** Every print and reprint path now resolves
  its printer through `ResolvePrinterName(request)`: an explicit
  per-request printer wins if one was passed, otherwise Settings'
  configured printer, otherwise `null` (SilentPrinter's own "system
  default printer").

### How to test each of the three things separately

Run the app from a terminal so you can see the listener's console output:
`dotnet run --project src/UltrasoundApp.Host`.

**A. AE Title / port**

1. Go to **Settings**. Confirm the AE Title and port fields are
   pre-filled (`ULTRASOUNDAPP` / `11112` on a fresh database, or whatever
   step 13's harness last saved).
2. Change the port to `11114` and click **Save**. Confirm the green
   message says the DICOM listener has been restarted, and the app's
   terminal logs `Listening for C-STORE on port 11114`.
3. Push a test file at the **new** port and confirm it's received, with no
   app restart in between:
   ```bash
   storescu -c ULTRASOUNDAPP@localhost:11114 path\to\test.dcm
   ```
4. Confirm the **old** port is genuinely released — the same command
   against `:11112` should now fail to connect.
5. Change the AE Title to `MYAE`, save, and confirm a push using the old
   AE Title is now rejected (the listener logs a called-AE-Title
   mismatch) while `storescu -c MYAE@localhost:11114 ...` succeeds.
6. **Persistence:** close and relaunch the app. Confirm Settings still
   shows `MYAE`/`11114` and the terminal logs those values at startup —
   no files edited by hand anywhere.
7. **Rebind-failure path:** occupy a port first (e.g.
   `dotnet run --project src/UltrasoundApp.Dicom.ManualTest -- OTHERAE 11115`),
   then in Settings change the port to `11115` and save. Confirm you get
   the red "saved, but the listener failed to rebind" warning, that the
   rest of the app still works (Manual DICOM Upload especially), and that
   reopening Settings still shows `11115` — the save was kept even though
   the rebind failed.

**B. Branding**

1. In **Settings → Hospital Branding**, change the hospital name to
   something unmistakable (e.g. `SETTINGS TEST HOSPITAL`), put two lines
   in Address, and set distinctive header/footer text. Click **Save**.
2. Go to **Patient List**, expand a study with images, click **Print
   Images**, and look at the preview. Confirm the header/footer show your
   new values — with no app restart and **without editing
   `templates/image-sheets/default-branded-template.json`**.
3. Confirm the JSON template still controls what Settings doesn't: the
   page size, margins, and grid density should be unchanged from before.
4. Leave one branding field blank (e.g. clear Header Text) and save.
   Confirm the preview falls back to the JSON template's header rather
   than printing an empty line.
5. **Persistence:** restart the app, print again, and confirm your
   branding is still applied.

**C. Printer selection**

1. In **Settings → Printer**, confirm the dropdown lists your installed
   Windows printers, plus a `(System default)` option. On a dev machine
   with no printers installed the list will be empty — that's expected,
   and step 3 below is how you verify the wiring anyway.
2. Pick a specific printer and click **Save**. Print an image sheet
   (Patient List → Print Images) and confirm the result message reports
   that printer as the one used — without passing a printer anywhere in
   the UI.
3. **On a machine with no printers:** confirm printing still succeeds via
   the saved-to-disk fallback (green message, PDF written to
   `data/print-fallback/`), exactly as before — the Settings wiring
   changes *which* printer is requested, not the fallback behavior.
4. **Check the print log:** the printer actually used is recorded per
   reprint in `PrintJobs` (step 10), so you can confirm it there too:
   ```sql
   SELECT Type, Timestamp, PrinterUsed FROM PrintJobs ORDER BY Timestamp DESC LIMIT 5;
   ```
5. **Persistence:** restart the app, reopen Settings, and confirm your
   printer is still selected; print again and confirm it's still the one
   used.
