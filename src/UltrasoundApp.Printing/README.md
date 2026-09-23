# UltrasoundApp.Printing

Image-sheet print **backend** (this step). References `UltrasoundApp.Core`
only. There is still no UI here — no `ImagePrintPage`, no thumbnail grid,
no preview. That's the next, dedicated step. This step is composer +
printer logic only, exercised via the manual test console app described
below.

## What's here

- **`ImageSheetComposer`** — given a list of selected image file paths and
  an `ImageSheetBrandingTemplate`, composes them into a single print-ready
  PDF using [QuestPDF](https://www.questpdf.com/). The grid (columns ×
  rows) is computed from the image count for each page
  (`ImageSheetComposer.CalculateGridDimensions`) — there is no single
  hardcoded layout. If more images are selected than
  `MaxImagesPerPage` (from the branding template), the composer spills the
  rest onto additional pages, each auto-gridded the same way.
- **`ImageSheetBrandingTemplate`** / **`ImageSheetBrandingTemplateLoader`**
  — the hospital branding + layout info (logo, name, address, header/footer
  text, page size, margins, max images per page), loaded from
  `templates/image-sheets/default-branded-template.json`. A missing or
  malformed template file falls back to a built-in default rather than
  crashing.
- **`SilentPrinter`** — sends a generated PDF to a printer without an OS
  print dialog. See the class's XML doc comments for exactly which code
  path it takes and why "silent" is best-effort in one of those paths;
  short version: with no real printer installed (the expected dev-machine
  case), it automatically falls back to saving the PDF under
  `data/print-fallback/` and opening it for manual inspection.

### Why QuestPDF over SkiaSharp

QuestPDF's fluent, declarative layout API (`Row`/`Column`/`Table` with
relative sizing) is a direct fit for "a grid that reflows itself based on
how many images are selected." SkiaSharp is an immediate-mode 2D canvas —
building the same reflowing grid with it would mean hand-computing every
cell's pixel rectangle, plus bolting on `SKDocument.CreatePdf` afterwards
since SkiaSharp doesn't target PDF output the way QuestPDF does natively.

**Licensing note:** QuestPDF's Community license is free only for
organizations under $1M USD annual gross revenue
([pricing](https://www.questpdf.com/pricing.html)); larger organizations
need a paid license. `ImageSheetComposer` sets
`QuestPDF.Settings.License = LicenseType.Community` for this prototype
stage — revisit before shipping to a hospital customer above that
threshold.

## Manual test harness

There's a throwaway console project,
`src/UltrasoundApp.Printing.ManualTest`, for exactly what this step asks
for: exercising the composer and printer directly without any UI. See its
own README / the root `docs/README.md` for how to run it.

## Known limitation (by design, for this step)

`SilentPrinter`'s fully-silent path requires a small external command-line
helper (SumatraPDF-style `-print-to -silent`) that isn't bundled yet —
`SilentPrintHelperExecutablePath` is left `null` for now, so real hardware
testing will exercise the "printto" shell-verb best-effort path or the
disk-fallback path, not the helper path. Wiring up a bundled helper (or a
PDF rendering SDK) is a reasonable follow-up once real printer hardware is
available to validate against.
