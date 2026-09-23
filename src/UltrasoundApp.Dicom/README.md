# UltrasoundApp.Dicom

DICOM parsing/extraction only — uses [fo-dicom](https://github.com/fo-dicom/fo-dicom)
(`fo-dicom` + `fo-dicom.Imaging.ImageSharp`, cross-platform, no GDI/Windows
dependency) to read `.dcm` files.

- `DicomFileParser.cs` — given a `.dcm` file path, extracts patient/study/
  image metadata (Patient ID, Patient Name, DOB/Age, Sex,
  StudyInstanceUID, ExamType, StudyDate/Time, SOPInstanceUID), renders the
  pixel data (first frame) to a PNG, and writes a bounded-size thumbnail
  PNG alongside it under `data/images-extracted/{StudyInstanceUID}/`.
  Returns a `DicomParseResult` (reusing the `UltrasoundApp.Core.Models`
  types as a plain in-memory DTO bundle).
- `ImageStoragePaths.cs` — resolves `data/images-extracted/`, anchored on
  the repo's `UltrasoundApp.sln`. Self-contained (does not reference
  `UltrasoundApp.Data`).
- `FoDicomBootstrapper.cs` — one-time fo-dicom setup (registers the
  ImageSharp-backed image manager needed to render pixel data).

Notes / assumptions:
- "ExamType" has no single standard DICOM tag; this uses
  `StudyDescription`, falling back to `Modality` if blank.
- `Study.Status` isn't a DICOM concept — left blank here for the future
  processing/insert service to set.
- Only the first frame is rendered for multi-frame images.

This step does **not** write to the database, build any UI, or implement
the processing/insert service — those are separate future steps. Nothing
here is persisted anywhere except the rendered image + thumbnail files.
