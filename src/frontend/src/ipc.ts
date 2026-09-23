/**
 * Thin wrapper around the WebView2 native bridge.
 *
 * This is scaffolding only: it proves the round-trip between React and the
 * C# host works. Real message types / request-response handlers will be
 * added feature-by-feature in later sessions — do not add business logic
 * here yet.
 *
 * Wire format: plain JSON strings, envelope shape { type: string }.
 */

interface BridgeMessage {
  type: string;
}

interface WebView2Api {
  postMessage: (message: string) => void;
  addEventListener: (
    type: "message",
    listener: (event: MessageEvent<string>) => void
  ) => void;
  removeEventListener: (
    type: "message",
    listener: (event: MessageEvent<string>) => void
  ) => void;
}

declare global {
  interface Window {
    chrome?: {
      webview?: WebView2Api;
    };
  }
}

function getBridge(): WebView2Api | undefined {
  return window.chrome?.webview;
}

/** True when running inside the WebView2 host (vs. a plain browser tab). */
export function isBridgeAvailable(): boolean {
  return getBridge() !== undefined;
}

/**
 * Reply shape for the "uploadDicom" request. `success` tells you whether
 * the file was parsed and written to the database; `cancelled` is set
 * instead of an error if the user closed the file picker without
 * choosing a file.
 */
export interface UploadDicomResult {
  type: "uploadDicomResult";
  success: boolean;
  cancelled?: boolean;
  error?: string;
  fileName?: string;
  patientId?: string;
  patientName?: string;
  studyInstanceUid?: string;
  examType?: string;
  sopInstanceUid?: string;
  patientWasCreated?: boolean;
  studyWasCreated?: boolean;
  imageWasCreated?: boolean;
}

/**
 * Sends a single "ping" to the native host and resolves once the matching
 * "pong" reply comes back. Resolves to false if there is no bridge
 * available (e.g. running the frontend standalone in a regular browser).
 */
export function sendPing(): Promise<boolean> {
  const bridge = getBridge();
  if (!bridge) {
    return Promise.resolve(false);
  }

  return new Promise<boolean>((resolve) => {
    const handleMessage = (event: MessageEvent<string>) => {
      let message: BridgeMessage;
      try {
        message = JSON.parse(event.data);
      } catch {
        return;
      }

      if (message.type === "pong") {
        bridge.removeEventListener("message", handleMessage);
        resolve(true);
      }
    };

    bridge.addEventListener("message", handleMessage);
    bridge.postMessage(JSON.stringify({ type: "ping" } satisfies BridgeMessage));
  });
}

/**
 * Asks the native host to show a file picker for a .dcm file, then parse
 * it and write it to the database. Resolves once the host replies with
 * the outcome — including a cancelled pick or a processing error, which
 * are both reported via the resolved value rather than a rejection, so
 * callers can render them the same way as any other result.
 */
export function uploadDicomFile(): Promise<UploadDicomResult> {
  const bridge = getBridge();
  if (!bridge) {
    return Promise.resolve({
      type: "uploadDicomResult",
      success: false,
      error: "The native bridge isn't available. Run this inside the desktop app to upload DICOM files."
    });
  }

  return new Promise<UploadDicomResult>((resolve) => {
    const handleMessage = (event: MessageEvent<string>) => {
      let message: UploadDicomResult;
      try {
        message = JSON.parse(event.data);
      } catch {
        return;
      }

      if (message.type === "uploadDicomResult") {
        bridge.removeEventListener("message", handleMessage);
        resolve(message);
      }
    };

    bridge.addEventListener("message", handleMessage);
    bridge.postMessage(JSON.stringify({ type: "uploadDicom" } satisfies BridgeMessage));
  });
}

/**
 * One DICOM tag as reported by "inspectDicomFile". `value` is an empty
 * string when the tag is present in the file but has no value entered
 * (as opposed to the tag not existing at all, which simply doesn't
 * appear in `fields`). `keyword` is the token to use if this field is
 * later wired into a report template; `path` is empty for a top-level
 * tag, or names the sequence/item this tag was nested under.
 */
export interface DicomFieldInfo {
  tag: string;
  keyword: string;
  name: string;
  vr: string;
  value: string;
  category: string;
  path: string;
}

/**
 * Reply shape for the "inspectDicomFile" request. `success` tells you
 * whether the file was opened and read; `cancelled` is set instead of an
 * error if the user closed the file picker without choosing a file.
 * Nothing about this request touches the database — it only reads and
 * reports what's in the file.
 */
export interface InspectDicomFileResult {
  type: "inspectDicomFileResult";
  success: boolean;
  cancelled?: boolean;
  error?: string;
  fileName?: string;
  fields: DicomFieldInfo[];
}

/**
 * Asks the native host to show a file picker for any .dcm file, then
 * report back every tag it contains — for browsing what data is
 * available in a file, not for importing it. Backs the Settings screen's
 * "DICOM Data Check" tool.
 */
export function inspectDicomFile(): Promise<InspectDicomFileResult> {
  const bridge = getBridge();
  if (!bridge) {
    return Promise.resolve({
      type: "inspectDicomFileResult",
      success: false,
      error: "The native bridge isn't available. Run this inside the desktop app to inspect DICOM files.",
      fields: []
    });
  }

  return new Promise<InspectDicomFileResult>((resolve) => {
    const handleMessage = (event: MessageEvent<string>) => {
      let message: InspectDicomFileResult;
      try {
        message = JSON.parse(event.data);
      } catch {
        return;
      }

      if (message.type === "inspectDicomFileResult") {
        bridge.removeEventListener("message", handleMessage);
        resolve(message);
      }
    };

    bridge.addEventListener("message", handleMessage);
    bridge.postMessage(JSON.stringify({ type: "inspectDicomFile" } satisfies BridgeMessage));
  });
}

/**
 * One image belonging to a study. `thumbnailUrl`/`imageUrl` are
 * browser-loadable ("https://appimages/...") — use those for <img> tags.
 * `filePath`/`thumbnailPath` are the raw on-disk paths the host reported
 * them from and are mainly useful for debugging; either URL field may be
 * undefined if the host couldn't resolve a path.
 */
export interface PatientStudyImage {
  sopInstanceUid: string;
  filePath: string;
  thumbnailPath?: string;
  imageUrl?: string;
  thumbnailUrl?: string;
}

/** One row of the patient list: a single study, with its patient info and images nested underneath. */
export interface PatientStudyRecord {
  patientId: string;
  patientName: string;
  dateOfBirth?: string;
  age?: number;
  sex?: string;
  studyInstanceUid: string;
  examType: string;
  /** ISO 8601 timestamp (see Study.StudyDateTime on the host). */
  studyDateTime: string;
  status: string;
  /** Whether a report has already been generated for this study — gates the "Reprint Report" action. */
  hasGeneratedReport: boolean;
  images: PatientStudyImage[];
}

export interface PatientStudyQueryResult {
  success: boolean;
  error?: string;
  records: PatientStudyRecord[];
}

interface PatientStudyListReply {
  type: string;
  success: boolean;
  error?: string;
  records?: PatientStudyRecord[];
}

/** Shared plumbing for listPatientStudies / searchPatientStudies / filterPatientStudiesByDateRange. */
function requestPatientStudies(
  request: Record<string, unknown>,
  resultType: string
): Promise<PatientStudyQueryResult> {
  const bridge = getBridge();
  if (!bridge) {
    return Promise.resolve({
      success: false,
      error: "The native bridge isn't available. Run this inside the desktop app to load patient data.",
      records: []
    });
  }

  return new Promise<PatientStudyQueryResult>((resolve) => {
    const handleMessage = (event: MessageEvent<string>) => {
      let message: PatientStudyListReply;
      try {
        message = JSON.parse(event.data);
      } catch {
        return;
      }

      if (message.type === resultType) {
        bridge.removeEventListener("message", handleMessage);
        resolve({
          success: message.success,
          error: message.error,
          records: message.records ?? []
        });
      }
    };

    bridge.addEventListener("message", handleMessage);
    bridge.postMessage(JSON.stringify(request));
  });
}

/** Every study, with its patient and images. */
export function listPatientStudies(): Promise<PatientStudyQueryResult> {
  return requestPatientStudies({ type: "listPatientStudies" }, "listPatientStudiesResult");
}

/** Studies whose patient name or patient ID contains `searchText` (case-insensitive). */
export function searchPatientStudies(searchText: string): Promise<PatientStudyQueryResult> {
  return requestPatientStudies(
    { type: "searchPatientStudies", searchText },
    "searchPatientStudiesResult"
  );
}

/**
 * Studies whose date falls within [startDate, endDate] (both "yyyy-MM-dd",
 * inclusive, either omittable for an open-ended range).
 */
export function filterPatientStudiesByDateRange(
  startDate?: string,
  endDate?: string
): Promise<PatientStudyQueryResult> {
  return requestPatientStudies(
    { type: "filterPatientStudiesByDateRange", startDate, endDate },
    "filterPatientStudiesByDateRangeResult"
  );
}

/**
 * Reply shape for "composeImagePrintPreview". `previewUrl` is a
 * "https://printpreview/..." URL loadable directly into an <iframe> (see
 * MainForm's virtual-host mapping); it's present whenever `success` is
 * true. `columns`/`rows` describe the first page's grid only — later
 * pages may differ if more images were selected than fit on one page.
 */
export interface ComposeImagePrintPreviewResult {
  success: boolean;
  error?: string;
  previewUrl?: string;
  pageCount?: number;
  imagesComposed?: number;
  skippedCount?: number;
  columns?: number;
  rows?: number;
}

interface ComposeImagePrintPreviewReply extends ComposeImagePrintPreviewResult {
  type: string;
}

/**
 * Composes `imageFilePaths` (raw on-disk paths, as reported in a
 * PatientStudyImage's `filePath`) into a print-ready sheet and resolves
 * once the host replies with a URL for the resulting PDF. `gridLayout`,
 * when given, pins the sheet to that exact (columns, rows) grid instead of
 * the host's automatic near-square layout — this is how the operator's
 * "Grid Layout" dropdown selection actually changes the composed sheet.
 */
export function composeImagePrintPreview(
  imageFilePaths: string[],
  gridLayout?: { columns: number; rows: number }
): Promise<ComposeImagePrintPreviewResult> {
  const bridge = getBridge();
  if (!bridge) {
    return Promise.resolve({
      success: false,
      error: "The native bridge isn't available. Run this inside the desktop app to build a print preview."
    });
  }

  return new Promise<ComposeImagePrintPreviewResult>((resolve) => {
    const handleMessage = (event: MessageEvent<string>) => {
      let message: ComposeImagePrintPreviewReply;
      try {
        message = JSON.parse(event.data);
      } catch {
        return;
      }

      if (message.type === "composeImagePrintPreviewResult") {
        bridge.removeEventListener("message", handleMessage);
        const { type: _type, ...result } = message;
        resolve(result);
      }
    };

    bridge.addEventListener("message", handleMessage);
    bridge.postMessage(
      JSON.stringify({
        type: "composeImagePrintPreview",
        imageFilePaths,
        gridColumns: gridLayout?.columns,
        gridRows: gridLayout?.rows
      })
    );
  });
}

/** Reply shape for "printImageSheet". `mode` mirrors SilentPrintMode (SilentlyPrintedViaHelper / SilentlyPrintedViaShellVerb / SavedToDiskFallback). */
export interface PrintImageSheetResult {
  success: boolean;
  error?: string;
  mode?: string;
  printerUsed?: string;
  outputPath?: string;
  message?: string;
  pageCount?: number;
  imagesComposed?: number;
  skippedCount?: number;
}

interface PrintImageSheetReply extends PrintImageSheetResult {
  type: string;
}

/**
 * Composes `imageFilePaths` into a sheet and sends it to `printerName`
 * (or the system default printer if omitted) via SilentPrinter. `gridLayout`
 * pins the sheet to that exact (columns, rows) grid, same as
 * `composeImagePrintPreview` — pass the same value used for the preview so
 * what gets printed matches what was previewed. Resolves once the host
 * reports the outcome — including a "saved to disk instead" fallback,
 * which is reported as `success: true` with `mode: "SavedToDiskFallback"`
 * since it's the expected result on a machine with no printer installed.
 */
export function printImageSheet(
  imageFilePaths: string[],
  printerName?: string,
  gridLayout?: { columns: number; rows: number },
  /** Print each image on a light-grey box (the preview's "Gray" option). Only takes effect when a template image area is selected. */
  grayBackground?: boolean,
  /**
   * Patient-information values (fieldKey -> text, see placeholderFields.ts)
   * for the placeholder boxes the operator placed on the image template.
   * Ignored by the host when no template / no placeholders are saved.
   */
  placeholderValues?: Record<string, string>,
  /**
   * The study being printed. After a successful print the host marks it
   * "ImagePrinted", so the Patient List moves it from the "Waiting Study" tab
   * to the "Image Printed" tab.
   */
  studyInstanceUid?: string
): Promise<PrintImageSheetResult> {
  const bridge = getBridge();
  if (!bridge) {
    return Promise.resolve({
      success: false,
      error: "The native bridge isn't available. Run this inside the desktop app to print."
    });
  }

  return new Promise<PrintImageSheetResult>((resolve) => {
    const handleMessage = (event: MessageEvent<string>) => {
      let message: PrintImageSheetReply;
      try {
        message = JSON.parse(event.data);
      } catch {
        return;
      }

      if (message.type === "printImageSheetResult") {
        bridge.removeEventListener("message", handleMessage);
        const { type: _type, ...result } = message;
        resolve(result);
      }
    };

    bridge.addEventListener("message", handleMessage);
    bridge.postMessage(
      JSON.stringify({
        type: "printImageSheet",
        imageFilePaths,
        printerName,
        gridColumns: gridLayout?.columns,
        gridRows: gridLayout?.rows,
        grayBackground,
        placeholderValues,
        studyInstanceUid
      })
    );
  });
}

/**
 * Reply shape for "getReportFormFields". `manualFields` is the list of
 * exam-specific placeholder keys (e.g. "LIVER_SIZE", "IMPRESSION") the
 * exam type's report template needs beyond the DICOM auto-filled ones —
 * exactly what ReportForm should render an input for.
 */
export interface ReportFormFieldsResult {
  success: boolean;
  error?: string;
  examType?: string;
  manualFields?: string[];
}

interface ReportFormFieldsReply extends ReportFormFieldsResult {
  type: string;
}

/** Looks up the exam type's report template and returns which manual fields it needs. */
export function getReportFormFields(examType: string): Promise<ReportFormFieldsResult> {
  const bridge = getBridge();
  if (!bridge) {
    return Promise.resolve({
      success: false,
      error: "The native bridge isn't available. Run this inside the desktop app to load the report form."
    });
  }

  return new Promise<ReportFormFieldsResult>((resolve) => {
    const handleMessage = (event: MessageEvent<string>) => {
      let message: ReportFormFieldsReply;
      try {
        message = JSON.parse(event.data);
      } catch {
        return;
      }

      if (message.type === "getReportFormFieldsResult") {
        bridge.removeEventListener("message", handleMessage);
        const { type: _type, ...result } = message;
        resolve(result);
      }
    };

    bridge.addEventListener("message", handleMessage);
    bridge.postMessage(JSON.stringify({ type: "getReportFormFields", examType }));
  });
}

/** Reply shape for "getSavedReportFieldValues" — the study's previously generated report's field values, if any. */
export interface SavedReportFieldValuesResult {
  success: boolean;
  error?: string;
  fieldValues?: Record<string, string>;
}

interface SavedReportFieldValuesReply extends SavedReportFieldValuesResult {
  type: string;
}

/** Fetches the study's previously saved report field values (both auto-filled and manual), so re-opening a study resumes its previous draft instead of starting blank. */
export function getSavedReportFieldValues(studyInstanceUid: string): Promise<SavedReportFieldValuesResult> {
  const bridge = getBridge();
  if (!bridge) {
    return Promise.resolve({
      success: false,
      error: "The native bridge isn't available. Run this inside the desktop app to load saved report values."
    });
  }

  return new Promise<SavedReportFieldValuesResult>((resolve) => {
    const handleMessage = (event: MessageEvent<string>) => {
      let message: SavedReportFieldValuesReply;
      try {
        message = JSON.parse(event.data);
      } catch {
        return;
      }

      if (message.type === "getSavedReportFieldValuesResult") {
        bridge.removeEventListener("message", handleMessage);
        const { type: _type, ...result } = message;
        resolve(result);
      }
    };

    bridge.addEventListener("message", handleMessage);
    bridge.postMessage(JSON.stringify({ type: "getSavedReportFieldValues", studyInstanceUid }));
  });
}

/**
 * Reply shape for "composeReportPreview". `previewUrl` is a
 * "https://printpreview/..." URL loadable directly into an <iframe>, present
 * whenever `success` is true. `missingFields`/`unusedFields` mirror
 * ReportRenderOutcome — surfaced so the UI can flag an incomplete report
 * before printing.
 */
export interface ComposeReportPreviewResult {
  success: boolean;
  error?: string;
  previewUrl?: string;
  missingFields?: string[];
  unusedFields?: string[];
}

interface ComposeReportPreviewReply extends ComposeReportPreviewResult {
  type: string;
}

/**
 * Merges `manualFieldValues` into the study's exam-type template via
 * ReportRenderService (on the host side) and resolves once a fresh preview
 * PDF has been rendered.
 */
export function composeReportPreview(
  studyInstanceUid: string,
  manualFieldValues: Record<string, string>
): Promise<ComposeReportPreviewResult> {
  const bridge = getBridge();
  if (!bridge) {
    return Promise.resolve({
      success: false,
      error: "The native bridge isn't available. Run this inside the desktop app to build a report preview."
    });
  }

  return new Promise<ComposeReportPreviewResult>((resolve) => {
    const handleMessage = (event: MessageEvent<string>) => {
      let message: ComposeReportPreviewReply;
      try {
        message = JSON.parse(event.data);
      } catch {
        return;
      }

      if (message.type === "composeReportPreviewResult") {
        bridge.removeEventListener("message", handleMessage);
        const { type: _type, ...result } = message;
        resolve(result);
      }
    };

    bridge.addEventListener("message", handleMessage);
    bridge.postMessage(
      JSON.stringify({ type: "composeReportPreview", studyInstanceUid, manualFieldValues })
    );
  });
}

/** Reply shape for "printReport". `mode` mirrors SilentPrintMode, same as `PrintImageSheetResult`. */
export interface PrintReportResult {
  success: boolean;
  error?: string;
  mode?: string;
  printerUsed?: string;
  outputPath?: string;
  message?: string;
  missingFields?: string[];
  unusedFields?: string[];
}

interface PrintReportReply extends PrintReportResult {
  type: string;
}

/**
 * Generates the final report PDF (via ReportRenderService) and sends it to
 * `printerName` (or the system default printer if omitted) via
 * SilentPrinter — the same fallback-to-disk behavior as `printImageSheet`
 * applies when no printer is installed.
 */
export function printReport(
  studyInstanceUid: string,
  manualFieldValues: Record<string, string>,
  printerName?: string
): Promise<PrintReportResult> {
  const bridge = getBridge();
  if (!bridge) {
    return Promise.resolve({
      success: false,
      error: "The native bridge isn't available. Run this inside the desktop app to print."
    });
  }

  return new Promise<PrintReportResult>((resolve) => {
    const handleMessage = (event: MessageEvent<string>) => {
      let message: PrintReportReply;
      try {
        message = JSON.parse(event.data);
      } catch {
        return;
      }

      if (message.type === "printReportResult") {
        bridge.removeEventListener("message", handleMessage);
        const { type: _type, ...result } = message;
        resolve(result);
      }
    };

    bridge.addEventListener("message", handleMessage);
    bridge.postMessage(
      JSON.stringify({ type: "printReport", studyInstanceUid, manualFieldValues, printerName })
    );
  });
}

/**
 * Reply shape for "reprintImages". `mode` mirrors SilentPrintMode, same as
 * `PrintImageSheetResult`.
 */
export interface ReprintImagesResult {
  success: boolean;
  error?: string;
  mode?: string;
  printerUsed?: string;
  outputPath?: string;
  message?: string;
  pageCount?: number;
  imagesComposed?: number;
  skippedCount?: number;
}

interface ReprintImagesReply extends ReprintImagesResult {
  type: string;
}

/**
 * Reprints a study's already-extracted images, looked up on the host side
 * from the persisted record — no image selection is sent, since a reprint
 * always uses exactly what's already stored for that study (no DICOM
 * re-receive needed). Logs a new print-history entry distinct from the
 * original print.
 */
export function reprintImages(studyInstanceUid: string, printerName?: string): Promise<ReprintImagesResult> {
  const bridge = getBridge();
  if (!bridge) {
    return Promise.resolve({
      success: false,
      error: "The native bridge isn't available. Run this inside the desktop app to print."
    });
  }

  return new Promise<ReprintImagesResult>((resolve) => {
    const handleMessage = (event: MessageEvent<string>) => {
      let message: ReprintImagesReply;
      try {
        message = JSON.parse(event.data);
      } catch {
        return;
      }

      if (message.type === "reprintImagesResult") {
        bridge.removeEventListener("message", handleMessage);
        const { type: _type, ...result } = message;
        resolve(result);
      }
    };

    bridge.addEventListener("message", handleMessage);
    bridge.postMessage(JSON.stringify({ type: "reprintImages", studyInstanceUid, printerName }));
  });
}

/**
 * Reply shape for "reprintReport". `regenerated` is false when the
 * previously-generated PDF was reprinted exactly as-is, true when it had
 * to be rebuilt first (edited fields, or the saved PDF was missing).
 */
export interface ReprintReportResult {
  success: boolean;
  error?: string;
  mode?: string;
  printerUsed?: string;
  outputPath?: string;
  message?: string;
  regenerated?: boolean;
}

interface ReprintReportReply extends ReprintReportResult {
  type: string;
}

/**
 * Reprints a study's report from locally saved data only. Omit
 * `manualFieldValues` for a quick, no-edit reprint of the exact
 * previously-generated PDF (falling back to a from-saved-values rebuild
 * only if that PDF is no longer on disk). Pass `manualFieldValues` (e.g.
 * from a reprint-and-edit form pre-filled via `getSavedReportFieldValues`)
 * to rebuild the report with those edits before printing. Either way, this
 * never needs the original DICOM file, and logs a new print-history entry
 * distinct from the original print.
 */
export function reprintReport(
  studyInstanceUid: string,
  manualFieldValues?: Record<string, string>,
  printerName?: string
): Promise<ReprintReportResult> {
  const bridge = getBridge();
  if (!bridge) {
    return Promise.resolve({
      success: false,
      error: "The native bridge isn't available. Run this inside the desktop app to print."
    });
  }

  return new Promise<ReprintReportResult>((resolve) => {
    const handleMessage = (event: MessageEvent<string>) => {
      let message: ReprintReportReply;
      try {
        message = JSON.parse(event.data);
      } catch {
        return;
      }

      if (message.type === "reprintReportResult") {
        bridge.removeEventListener("message", handleMessage);
        const { type: _type, ...result } = message;
        resolve(result);
      }
    };

    bridge.addEventListener("message", handleMessage);
    bridge.postMessage(
      JSON.stringify({ type: "reprintReport", studyInstanceUid, manualFieldValues, printerName })
    );
  });
}

/**
 * App-wide configuration — AE Title/port, template folder overrides,
 * printer selection, and hospital branding fields. Mirrors
 * UltrasoundApp.Core.Models.AppSettings/WebViewBridge's SettingsDto
 * field-for-field. All the folder-path/printer/logo/header/footer fields
 * are optional: blank means "use whatever built-in default the host
 * already falls back to."
 */
export interface AppSettings {
  aeTitle: string;
  listeningPort: number;
  reportTemplatesFolderPath?: string;
  imageSheetBrandingTemplatePath?: string;
  printerName?: string;
  logoPath?: string;
  hospitalName: string;
  address: string;
  headerText?: string;
  footerText?: string;
}

/** Reply shape for "getSettings". */
export interface GetSettingsResult {
  success: boolean;
  error?: string;
  settings?: AppSettings;
}

interface GetSettingsReply extends GetSettingsResult {
  type: string;
}

/**
 * Loads the current app settings. SettingsService always applies
 * built-in defaults if nothing has ever been saved, so this only fails
 * (success: false) if the native bridge itself isn't available.
 */
export function getSettings(): Promise<GetSettingsResult> {
  const bridge = getBridge();
  if (!bridge) {
    return Promise.resolve({
      success: false,
      error: "The native bridge isn't available. Run this inside the desktop app to load settings."
    });
  }

  return new Promise<GetSettingsResult>((resolve) => {
    const handleMessage = (event: MessageEvent<string>) => {
      let message: GetSettingsReply;
      try {
        message = JSON.parse(event.data);
      } catch {
        return;
      }

      if (message.type === "getSettingsResult") {
        bridge.removeEventListener("message", handleMessage);
        const { type: _type, ...result } = message;
        resolve(result);
      }
    };

    bridge.addEventListener("message", handleMessage);
    bridge.postMessage(JSON.stringify({ type: "getSettings" }));
  });
}

/**
 * Reply shape for "saveSettings". `listenerRestarted` is true when the AE
 * Title/port actually changed and the host rebound the DICOM listener to
 * the new values right away (no app restart needed). `listenerRunning`
 * reflects whether the listener is actually up afterward — this can be
 * false even when `success` is true (the settings still saved) if, e.g.,
 * the new port turned out to already be in use.
 */
export interface SaveSettingsResult {
  success: boolean;
  error?: string;
  settings?: AppSettings;
  listenerRestarted?: boolean;
  listenerRunning?: boolean;
  /** Why the listener isn't running, when `listenerRunning` is false. */
  listenerError?: string;
}

interface SaveSettingsReply extends SaveSettingsResult {
  type: string;
}

/** Persists `settings`, replacing whatever was saved before, and returns exactly what was saved back (see {@link SaveSettingsResult}). */
export function saveSettings(settings: AppSettings): Promise<SaveSettingsResult> {
  const bridge = getBridge();
  if (!bridge) {
    return Promise.resolve({
      success: false,
      error: "The native bridge isn't available. Run this inside the desktop app to save settings."
    });
  }

  return new Promise<SaveSettingsResult>((resolve) => {
    const handleMessage = (event: MessageEvent<string>) => {
      let message: SaveSettingsReply;
      try {
        message = JSON.parse(event.data);
      } catch {
        return;
      }

      if (message.type === "saveSettingsResult") {
        bridge.removeEventListener("message", handleMessage);
        const { type: _type, ...result } = message;
        resolve(result);
      }
    };

    bridge.addEventListener("message", handleMessage);
    bridge.postMessage(JSON.stringify({ type: "saveSettings", settings }));
  });
}

/** Reply shape for "listPrinters". */
export interface ListPrintersResult {
  success: boolean;
  error?: string;
  printerNames?: string[];
}

interface ListPrintersReply extends ListPrintersResult {
  type: string;
}

/** Names of every printer Windows currently has installed, for the Settings screen's printer dropdown. */
export function listPrinters(): Promise<ListPrintersResult> {
  const bridge = getBridge();
  if (!bridge) {
    return Promise.resolve({
      success: false,
      error: "The native bridge isn't available. Run this inside the desktop app to list printers."
    });
  }

  return new Promise<ListPrintersResult>((resolve) => {
    const handleMessage = (event: MessageEvent<string>) => {
      let message: ListPrintersReply;
      try {
        message = JSON.parse(event.data);
      } catch {
        return;
      }

      if (message.type === "listPrintersResult") {
        bridge.removeEventListener("message", handleMessage);
        const { type: _type, ...result } = message;
        resolve(result);
      }
    };

    bridge.addEventListener("message", handleMessage);
    bridge.postMessage(JSON.stringify({ type: "listPrinters" }));
  });
}
