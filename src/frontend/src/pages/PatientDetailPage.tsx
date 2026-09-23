import { useEffect, useMemo, useState } from "react";
import ThumbnailGrid from "../components/ThumbnailGrid";
import LiveImageSheetPreview from "../components/LiveImageSheetPreview";
import StateMessage, { Spinner } from "../components/StateMessage";
import "../styles/patient-detail-layout.css";
import {
  printImageSheet,
  reprintReport,
  searchPatientStudies,
  type PatientStudyImage,
  type PatientStudyRecord
} from "../ipc";
import { subscribeHostEvent } from "../ipcLive";
import { getImageTemplate, type ContentArea, type TemplatePlaceholder } from "../ipcImageTemplate";
import { buildPlaceholderValues } from "../placeholderFields";
import { STUDY_STATUS_IMAGE_PRINTED, studyStatusLabel } from "../studyStatus";

interface PatientDetailPageProps {
  /** The study opened from Patient List by clicking a row. Null until one has been chosen. */
  study: PatientStudyRecord | null;
  /** Returns to the Patient List page. */
  onBack: () => void;
  /**
   * Navigates to the Report Print page with this study loaded (fresh
   * report). Not currently wired to a button on this page (the "Print
   * Report" button was removed) — kept in the prop type so App.tsx's
   * existing wiring doesn't need to change, and as the hook for a future
   * entry point if fresh-report generation needs a home again.
   */
  onPrintReport: (record: PatientStudyRecord) => void;
  /** Navigates to the Report Print page in reprint mode, pre-loaded with this study's saved report values. */
  onEditAndReprintReport: (record: PatientStudyRecord) => void;
}

type PrintStatus =
  | { kind: "idle" }
  | { kind: "printing" }
  | { kind: "result"; success: boolean; message: string };

type ReprintStatus =
  | { kind: "idle" }
  | { kind: "working" }
  | { kind: "result"; success: boolean; message: string };

/** One grid-layout dropdown option. `value` is what's persisted; `null` layout means "Auto" (the host's own near-square grid). */
interface GridLayoutOption {
  value: string;
  label: string;
  layout: { columns: number; rows: number } | null;
}

/**
 * Grid layout choices offered in the "Grid Layout" dropdown. "Auto" keeps
 * the host's existing near-square-grid behavior (ImageSheetComposer.
 * CalculateGridDimensions); every other entry pins the composed sheet to
 * that exact columns×rows grid regardless of how many images are
 * selected, spilling extra images onto additional pages of the same grid.
 */
const GRID_LAYOUT_OPTIONS: GridLayoutOption[] = [
  { value: "auto", label: "Auto (best fit)", layout: null },
  { value: "1x1", label: "1 × 1 (1 per page)", layout: { columns: 1, rows: 1 } },
  { value: "2x1", label: "2 × 1 (2 per page)", layout: { columns: 2, rows: 1 } },
  { value: "1x2", label: "1 × 2 (2 per page)", layout: { columns: 1, rows: 2 } },
  { value: "2x2", label: "2 × 2 (4 per page)", layout: { columns: 2, rows: 2 } },
  { value: "3x2", label: "3 × 2 (6 per page)", layout: { columns: 3, rows: 2 } },
  { value: "2x3", label: "2 × 3 (6 per page)", layout: { columns: 2, rows: 3 } },
  { value: "3x3", label: "3 × 3 (9 per page)", layout: { columns: 3, rows: 3 } },
  { value: "4x3", label: "4 × 3 (12 per page)", layout: { columns: 4, rows: 3 } },
  { value: "3x4", label: "3 × 4 (12 per page)", layout: { columns: 3, rows: 4 } },
  { value: "4x4", label: "4 × 4 (16 per page)", layout: { columns: 4, rows: 4 } },
  { value: "4x5", label: "4 × 5 (20 per page)", layout: { columns: 4, rows: 5 } },
  { value: "5x4", label: "5 × 4 (20 per page)", layout: { columns: 5, rows: 4 } },
  { value: "6x4", label: "6 × 4 (24 per page)", layout: { columns: 6, rows: 4 } },
  { value: "6x5", label: "6 × 5 (30 per page)", layout: { columns: 6, rows: 5 } }
];

/** localStorage key the chosen grid layout is remembered under, per machine — so the operator's choice sticks across studies and app restarts. */
const GRID_LAYOUT_STORAGE_KEY = "usg.patientDetail.gridLayout";

function loadSavedGridLayoutValue(): string {
  try {
    const saved = window.localStorage.getItem(GRID_LAYOUT_STORAGE_KEY);
    if (saved && GRID_LAYOUT_OPTIONS.some((option) => option.value === saved)) {
      return saved;
    }
  } catch {
    // localStorage can throw (e.g. disabled/private browsing) — fall through to the default.
  }
  return "auto";
}

/** localStorage key for the "Single Page" checkbox, so it stays as the operator left it. */
const SINGLE_PAGE_STORAGE_KEY = "usg.patientDetail.singlePage";

function loadSavedSinglePage(): boolean {
  try {
    return window.localStorage.getItem(SINGLE_PAGE_STORAGE_KEY) === "1";
  } catch {
    return false;
  }
}

/** Same near-square "Auto" grid the live preview and the host use, for a given number of images. */
function computeAutoGrid(imageCount: number): { columns: number; rows: number } {
  const columns = Math.max(1, Math.ceil(Math.sqrt(imageCount)));
  const rows = Math.max(1, Math.ceil(imageCount / columns));
  return { columns, rows };
}

/** localStorage key for the "Gray" checkbox (dark box behind each image, image-area mode only). */
const GRAY_BOX_STORAGE_KEY = "usg.patientDetail.grayBox";

function loadSavedGrayBox(): boolean {
  try {
    return window.localStorage.getItem(GRAY_BOX_STORAGE_KEY) === "1";
  } catch {
    return false;
  }
}

function formatDateTime(iso: string): string {
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) {
    return iso;
  }
  return date.toLocaleString(undefined, {
    year: "numeric",
    month: "short",
    day: "numeric",
    hour: "numeric",
    minute: "2-digit"
  });
}

/**
 * Patient Detail page: opened by clicking a row in Patient List. Shows the
 * study's patient details up top, then immediately below it a two-column
 * workspace — an instant, client-side preview of the current selection on
 * the left (LiveImageSheetPreview) laid out in the chosen grid, and every
 * image in the study on the right (ThumbnailGrid, toggle to
 * select/deselect; nothing is pre-selected, so opening a study never
 * triggers an automatic full-selection preview). The preview updates the
 * moment a thumbnail is toggled, with no backend round-trip — the real,
 * pixel-exact sheet is still composed server-side (ImageSheetComposer) at
 * actual print time via printImageSheet, so this instant preview never
 * blocks or slows down printing. Images appear in the preview in the
 * order they were selected (not the study's original order). The "Grid
 * Layout" dropdown (next to the "Print Preview" heading) lets the operator
 * pin the sheet to a specific columns×rows grid instead of the automatic
 * near-square one — remembered via localStorage across studies and app
 * restarts. The "Single Page" checkbox next to it lets the whole preview
 * fit in view without an internal scrollbar. When the study already has a
 * saved report, "Reprint Report" / "Edit & Reprint" appear below the
 * workspace. If the Settings screen has an "Image Template" PDF uploaded,
 * its (cached, rasterized) first page is drawn as a full-page background
 * behind the preview's grid — see LiveImageSheetPreview's
 * backgroundImageUrl prop — and the same template is composed into the
 * actual printed PDF (host-side ImageSheetComposer), so what's shown here
 * matches what comes out of the printer.
 */
/** True when nothing this page shows has changed between two copies of the same study. */
function sameStudyContent(a: PatientStudyRecord | null, b: PatientStudyRecord): boolean {
  if (!a) {
    return false;
  }
  return (
    a.status === b.status &&
    a.hasGeneratedReport === b.hasGeneratedReport &&
    a.images.length === b.images.length &&
    a.images.every((image, index) => image.sopInstanceUid === b.images[index].sopInstanceUid)
  );
}

export default function PatientDetailPage({ study: studyProp, onBack, onEditAndReprintReport }: PatientDetailPageProps) {
  // The study as opened from Patient List, kept up to date while this page is open: when the machine sends
  // more images for this same study, `liveStudy` gets the new record (see the effect below).
  const [liveStudy, setLiveStudy] = useState<PatientStudyRecord | null>(null);
  const study =
    liveStudy && studyProp && liveStudy.studyInstanceUid === studyProp.studyInstanceUid ? liveStudy : studyProp;

  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [gridLayoutValue, setGridLayoutValue] = useState<string>(() => loadSavedGridLayoutValue());
  const [singlePageFit, setSinglePageFit] = useState<boolean>(() => loadSavedSinglePage());
  const [grayBox, setGrayBox] = useState<boolean>(() => loadSavedGrayBox());
  const [printStatus, setPrintStatus] = useState<PrintStatus>({ kind: "idle" });
  const [reportReprintStatus, setReportReprintStatus] = useState<ReprintStatus>({ kind: "idle" });
  // Settings screen's uploaded "Image Template" background, if any — fetched
  // once per time this page is opened (cheap: just a directory lookup +
  // cached PNG path on the host side, no PDF rendering on this path) so
  // returning here after changing the template in Settings always picks up
  // the latest one. Toggling/selecting images never re-fetches this, so it
  // never adds any lag to the live preview.
  const [templateBackgroundUrl, setTemplateBackgroundUrl] = useState<string | undefined>(undefined);
  // The "image area" saved in Settings (Select Image Area): the rectangle of
  // the template page the images are confined to, so the template's header/
  // footer stay visible. Fetched together with the background above.
  const [templateContentArea, setTemplateContentArea] = useState<ContentArea | null>(null);
  // Patient-information boxes placed on the template in Settings (name, ID,
  // age, sex...). Filled with this study's patient data in the preview and
  // in the printed sheet.
  const [templatePlaceholders, setTemplatePlaceholders] = useState<TemplatePlaceholder[]>([]);

  useEffect(() => {
    let cancelled = false;
    getImageTemplate().then((result) => {
      if (!cancelled && result.success) {
        setTemplateBackgroundUrl(result.backgroundImageUrl);
        setTemplateContentArea(result.contentArea ?? null);
        setTemplatePlaceholders(result.placeholders ?? []);
      }
    });
    return () => {
      cancelled = true;
    };
  }, []);

  // Live update: when the host reports that something was received from the machine, re-read this study and
  // show any new images right away (no need to go back to Patient List and open it again). The images the
  // operator already selected stay selected; new ones simply appear in "All Images".
  const liveStudyUid = studyProp?.studyInstanceUid;
  const livePatientId = studyProp?.patientId;
  useEffect(() => {
    if (!liveStudyUid || !livePatientId) {
      return;
    }
    const studyUid: string = liveStudyUid;
    const patientId: string = livePatientId;

    let cancelled = false;
    let running = false;
    let pending = false;

    async function refresh() {
      if (running) {
        pending = true; // another update arrived mid-request: go once more afterwards
        return;
      }
      running = true;
      try {
        do {
          pending = false;
          const result = await Promise.race([
            searchPatientStudies(patientId),
            new Promise<null>((resolve) => window.setTimeout(() => resolve(null), 10000))
          ]);
          if (cancelled) {
            return;
          }
          if (result && result.success) {
            const fresh = result.records.find((record) => record.studyInstanceUid === studyUid);
            if (fresh) {
              setLiveStudy((previous) => (sameStudyContent(previous ?? studyProp, fresh) ? previous : fresh));
            }
          }
        } while (pending && !cancelled);
      } finally {
        running = false;
      }
    }

    const unsubscribe = subscribeHostEvent("studiesChanged", () => void refresh());
    function handleVisibility() {
      if (document.visibilityState === "visible") {
        void refresh();
      }
    }
    document.addEventListener("visibilitychange", handleVisibility);

    return () => {
      cancelled = true;
      unsubscribe();
      document.removeEventListener("visibilitychange", handleVisibility);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [liveStudyUid, livePatientId]);

  // The image area (and so the "Gray" option) only exists with a template + selected area.
  const hasImageArea = Boolean(templateBackgroundUrl && templateContentArea);

  // Text for every patient-information field, for this study's patient.
  const placeholderValues = useMemo(() => buildPlaceholderValues(study), [study]);

  const selectedGridLayout = useMemo(
    () => GRID_LAYOUT_OPTIONS.find((option) => option.value === gridLayoutValue)?.layout ?? null,
    [gridLayoutValue]
  );

  function handleSinglePageChange(checked: boolean) {
    setSinglePageFit(checked);
    try {
      window.localStorage.setItem(SINGLE_PAGE_STORAGE_KEY, checked ? "1" : "0");
    } catch {
      // Non-fatal.
    }
  }

  function handleGrayBoxChange(checked: boolean) {
    setGrayBox(checked);
    try {
      window.localStorage.setItem(GRAY_BOX_STORAGE_KEY, checked ? "1" : "0");
    } catch {
      // Non-fatal.
    }
  }

  function handleGridLayoutChange(value: string) {
    setGridLayoutValue(value);
    try {
      window.localStorage.setItem(GRID_LAYOUT_STORAGE_KEY, value);
    } catch {
      // Non-fatal — the choice just won't be remembered next time.
    }
  }

  // Set once this study's images have been printed from this page, so the
  // Status shown below updates right away (the list is refreshed when going back).
  const [printedStudyUid, setPrintedStudyUid] = useState<string | null>(null);

  // Nothing pre-selected whenever a different study is opened — the
  // operator picks only the images actually needed. Also clears any
  // leftover print/reprint status from a previous study.
  useEffect(() => {
    setSelected(new Set());
    setPrintStatus({ kind: "idle" });
    setReportReprintStatus({ kind: "idle" });
  }, [study?.studyInstanceUid]);

  // Looked up by sopInstanceUid so `selected`'s own iteration order (see
  // below) can drive the images' order, rather than the study's fixed
  // original order.
  const imageByUid = useMemo(() => {
    const map = new Map<string, PatientStudyImage>();
    for (const image of study?.images ?? []) {
      map.set(image.sopInstanceUid, image);
    }
    return map;
  }, [study]);

  // The selected images, in the order the operator selected them: a JS
  // Set iterates in insertion order, so walking `selected` (rather than
  // filtering study.images) means the preview lists images in click
  // order, not the study's original order — reselecting a deselected
  // image moves it to the end, same as "most recently added". Used two
  // ways below: the raw on-disk paths for printImageSheet (the host's
  // ImageSheetComposer needs those), and the browser-loadable URLs for
  // LiveImageSheetPreview. Reusing `thumbnailUrl` (the same one
  // ThumbnailGrid already rendered) means the live preview's <img>s load
  // from the browser's cache instead of fetching again, which is what
  // makes toggling a selection feel instant rather than "Composing
  // preview...".
  const selectedImages = useMemo(() => {
    const ordered: PatientStudyImage[] = [];
    for (const sopInstanceUid of selected) {
      const image = imageByUid.get(sopInstanceUid);
      if (image) {
        ordered.push(image);
      }
    }
    return ordered;
  }, [selected, imageByUid]);

  const selectedFilePaths = useMemo(() => selectedImages.map((image) => image.filePath), [selectedImages]);

  const livePreviewImages = useMemo(
    () =>
      selectedImages.map((image) => ({
        sopInstanceUid: image.sopInstanceUid,
        url: image.thumbnailUrl ?? image.imageUrl
      })),
    [selectedImages]
  );

  function toggleImage(sopInstanceUid: string) {
    setSelected((prev) => {
      const next = new Set(prev);
      if (next.has(sopInstanceUid)) {
        next.delete(sopInstanceUid);
      } else {
        next.add(sopInstanceUid);
      }
      return next;
    });
    setPrintStatus({ kind: "idle" });
  }

  function selectAll() {
    setSelected(new Set(study?.images.map((image) => image.sopInstanceUid) ?? []));
  }

  function selectNone() {
    setSelected(new Set());
  }

  async function handlePrint() {
    if (selectedFilePaths.length === 0) {
      return;
    }
    setPrintStatus({ kind: "printing" });
    // With an image area selected, pin the printed grid to exactly what the
    // preview shows (including the "Auto" case), so every row gets the same
    // share of the area on paper as on screen.
    const printLayout =
      selectedGridLayout ??
      (templateBackgroundUrl && templateContentArea ? computeAutoGrid(selectedFilePaths.length) : undefined);
    const result = await printImageSheet(
      selectedFilePaths,
      undefined,
      printLayout,
      hasImageArea && grayBox,
      placeholderValues,
      study?.studyInstanceUid
    );
    if (result.success && study) {
      // The host has moved this study to "Image Printed"; show that here too.
      setPrintedStudyUid(study.studyInstanceUid);
    }
    setPrintStatus({
      kind: "result",
      success: result.success,
      message: result.message ?? result.error ?? (result.success ? "Print job sent." : "Print failed.")
    });
  }

  async function handleReprintReportClick() {
    if (!study) {
      return;
    }
    setReportReprintStatus({ kind: "working" });
    // No manualFieldValues: reuses the study's saved report as-is.
    const result = await reprintReport(study.studyInstanceUid);
    setReportReprintStatus({
      kind: "result",
      success: result.success,
      message: result.message ?? result.error ?? (result.success ? "Reprint sent." : "Reprint failed.")
    });
  }

  if (!study) {
    return (
      <div className="page">
        <div className="patient-detail__header">
          <h1>Patient Details</h1>
          <button type="button" className="patient-detail__back-button" onClick={onBack}>
            ← Back to Patient List
          </button>
        </div>
        <StateMessage
          icon="🗂️"
          title="No patient loaded"
          detail="Select a patient's study from Patient List to view it here."
        />
      </div>
    );
  }

  return (
    <div className="page pd-page">
      <div className="pd-workspace">
        {/* LEFT (60%): print preview, starting at the very top */}
        <div className="pd-preview-pane">
          <div className="pd-preview-header">
            <h2 className="patient-detail__pane-title">Print Preview</h2>

            <div className="patient-detail__grid-layout">
              <label htmlFor="grid-layout-select">Grid Layout</label>
              <select
                id="grid-layout-select"
                value={gridLayoutValue}
                onChange={(event) => handleGridLayoutChange(event.target.value)}
              >
                {GRID_LAYOUT_OPTIONS.map((option) => (
                  <option key={option.value} value={option.value}>
                    {option.label}
                  </option>
                ))}
              </select>
            </div>

            <label className="pd-single-page-toggle">
              <input
                type="checkbox"
                checked={singlePageFit}
                onChange={(event) => handleSinglePageChange(event.target.checked)}
              />
              Single Page
            </label>

            <label
              className="pd-single-page-toggle"
              title={
                hasImageArea
                  ? "Print a dark box behind each image (same as shown in the preview)"
                  : "Available after you select an image area in Settings"
              }
            >
              <input
                type="checkbox"
                checked={hasImageArea && grayBox}
                disabled={!hasImageArea}
                onChange={(event) => handleGrayBoxChange(event.target.checked)}
              />
              Gray
            </label>
          </div>

          <LiveImageSheetPreview
            images={livePreviewImages}
            layout={selectedGridLayout}
            fitSinglePage={singlePageFit}
            backgroundImageUrl={templateBackgroundUrl}
            contentArea={templateContentArea}
            grayBackground={grayBox}
            placeholders={templatePlaceholders}
            placeholderValues={placeholderValues}
          />

          <div className="pd-preview-actions">
            <button
              type="button"
              className="image-print__print-button"
              onClick={handlePrint}
              disabled={selectedFilePaths.length === 0 || printStatus.kind === "printing"}
            >
              {printStatus.kind === "printing" ? <Spinner label="Printing..." /> : "Print"}
            </button>

            {printStatus.kind === "result" && (
              <p
                className={`image-print__print-status image-print__print-status--${
                  printStatus.success ? "success" : "error"
                }`}
              >
                {printStatus.success ? "✓" : "⚠️"} {printStatus.message}
              </p>
            )}
          </div>
        </div>

        {/* RIGHT (40%): patient info on top, then all images */}
        <div className="pd-side-pane">
          <div className="pd-side-header">
            <h1>Patient Details</h1>
            <button type="button" className="patient-detail__back-button" onClick={onBack}>
              ← Back to Patient List
            </button>
          </div>

          <div className="pd-info">
            <div className="patient-detail__info-field">
              <span className="patient-detail__info-label">Name</span>
              <span className="patient-detail__info-value">{study.patientName}</span>
            </div>
            <div className="patient-detail__info-field">
              <span className="patient-detail__info-label">Patient ID</span>
              <span className="patient-detail__info-value">{study.patientId}</span>
            </div>
            {study.age !== undefined && (
              <div className="patient-detail__info-field">
                <span className="patient-detail__info-label">Age</span>
                <span className="patient-detail__info-value">{study.age}</span>
              </div>
            )}
            {study.sex && (
              <div className="patient-detail__info-field">
                <span className="patient-detail__info-label">Sex</span>
                <span className="patient-detail__info-value">{study.sex}</span>
              </div>
            )}
            {study.dateOfBirth && (
              <div className="patient-detail__info-field">
                <span className="patient-detail__info-label">Date of Birth</span>
                <span className="patient-detail__info-value">{study.dateOfBirth}</span>
              </div>
            )}
            <div className="patient-detail__info-field">
              <span className="patient-detail__info-label">Exam Type</span>
              <span className="patient-detail__info-value">{study.examType}</span>
            </div>
            <div className="patient-detail__info-field">
              <span className="patient-detail__info-label">Date / Time</span>
              <span className="patient-detail__info-value">{formatDateTime(study.studyDateTime)}</span>
            </div>
            <div className="patient-detail__info-field">
              <span className="patient-detail__info-label">Status</span>
              <span className="patient-detail__info-value">
                {studyStatusLabel(
                  printedStudyUid === study.studyInstanceUid ? STUDY_STATUS_IMAGE_PRINTED : study.status
                )}
              </span>
            </div>
          </div>

          <h2 className="pd-images-title">All Images</h2>

          {study.images.length === 0 ? (
            <StateMessage
              icon="🖼️"
              title="No images in this study"
              detail="This study was recorded without any image instances, so there's nothing to select for an image sheet."
            />
          ) : (
            <>
              <div className="image-print__toolbar">
                <span>
                  {selected.size} of {study.images.length} selected
                </span>
                <button type="button" onClick={selectAll} disabled={selected.size === study.images.length}>
                  Select all
                </button>
                <button type="button" onClick={selectNone} disabled={selected.size === 0}>
                  Select none
                </button>
              </div>

              <ThumbnailGrid images={study.images} selected={selected} onToggle={toggleImage} />
            </>
          )}

          {study.hasGeneratedReport && (
            <div className="patient-detail__report-actions">
              <button
                type="button"
                className="patient-table__print-button patient-table__print-button--reprint"
                onClick={handleReprintReportClick}
                disabled={reportReprintStatus.kind === "working"}
                title="Reprint this study's saved report as-is — no re-entry, no DICOM re-receive."
              >
                {reportReprintStatus.kind === "working" ? <Spinner label="Reprinting..." /> : "Reprint Report"}
              </button>
              <button type="button" className="patient-table__link-button" onClick={() => onEditAndReprintReport(study)}>
                Edit &amp; Reprint
              </button>

              {reportReprintStatus.kind === "result" && (
                <div className="patient-table__reprint-status">
                  <p
                    className={`patient-table__reprint-message patient-table__reprint-message--${
                      reportReprintStatus.success ? "success" : "error"
                    }`}
                  >
                    {reportReprintStatus.success ? "✓" : "⚠️"} Report: {reportReprintStatus.message}
                  </p>
                </div>
              )}
            </div>
          )}
        </div>
      </div>
    </div>
  );
}
