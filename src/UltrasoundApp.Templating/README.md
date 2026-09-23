# UltrasoundApp.Templating

Report template backend — **no UI, no `ReportRenderService`, no
`ReportPrintPage` yet**; those are a later, dedicated step. This project
currently provides just the two building blocks a report pipeline needs:

- **`TemplateEngine`** — merges an HTML template (a file on disk, or a raw
  string) with a `Dictionary<string, string>` of field values. Placeholder
  tokens look like `{{PATIENT_NAME}}`, `{{LIVER_SIZE}}`, etc. Under the
  hood this uses [Scriban](https://github.com/scriban/scriban) (see the
  rationale comment in `UltrasoundApp.Templating.csproj`), but callers
  never need to know that. A token in the template with no matching
  dictionary entry renders blank rather than throwing or leaking a literal
  `{{TOKEN}}` into the output; `TemplateMergeResult.MissingFields` /
  `.UnusedFields` report any mismatch for the caller to act on.

- **`PdfRenderer`** — converts merged HTML into a PDF using WebView2's
  headless `PrintToPdfAsync`, with no visible window and no OS print
  dialog. It owns a dedicated hidden `Form` + background STA thread
  internally (see the class doc comment for why), so it can be called from
  a plain console app or from a WinForms UI thread equally well. Reuse one
  instance across multiple renders and `Dispose()` it when done.

Two example templates live in `templates/reports/`:
`whole-abdomen.html` and `pregnancy-profile.html`. Both set
`font-family: 'Nirmala UI', ...` so Bangla text renders correctly using
the Bengali-capable font Windows 10/11 ship out of the box.

See `docs/README.md` ("Report template backend") at the repo root for how
to test this with `UltrasoundApp.Templating.ManualTest`.
