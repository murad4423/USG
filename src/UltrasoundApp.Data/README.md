# UltrasoundApp.Data

SQLite data layer, using `Microsoft.Data.Sqlite` directly (plain ADO.NET —
no EF Core, no Dapper) for every repository.

- `AppPaths.cs` — resolves `data/app.db` relative to the repo root (finds
  `UltrasoundApp.sln` by walking up from the executable; falls back to a
  `data` folder next to the executable if no solution file is found, e.g.
  a standalone published build).
- `AppDbContext.cs` — holds the connection string and hands out opened,
  foreign-key-enforcing `SqliteConnection`s. Not an EF Core `DbContext`.
- `DbInitializer.cs` — idempotent schema creation (`CREATE TABLE/INDEX IF
  NOT EXISTS`) for all six entity tables. Safe to call on every startup.
- `Repositories/` — one repository (interface + implementation in the same
  file) per entity: `PatientRepository`, `StudyRepository`,
  `ImageRepository`, `ReportTemplateRepository`,
  `GeneratedReportRepository`, `PrintJobRepository`. Plain CRUD, no
  business logic.

No UI, DICOM parsing, printing, or templating logic lives here — see the
sibling projects for those.
