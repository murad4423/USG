import { useEffect, useState } from "react";
import ReportForm from "../components/ReportForm";
import StateMessage, { LoadingBlock, Spinner } from "../components/StateMessage";
import "../styles/patient-detail-layout.css";
import "../styles/report-preview-layout.css";
import { printReport, reprintReport, type PatientStudyImage, type PatientStudyRecord } from "../ipc";
import { studyStatusLabel } from "../studyStatus";
import { AUTO_FIELD_KEYS, AUTO_FIELD_LABELS, useReportDraft } from "../useReportDraft";

interface ReportPreviewPageProps {
  /** The study opened from the "Image Printed" tab of Patient List. Null until one has been chosen. */
  study: PatientStudyRecord | null;
  /** Returns to the Patient List page. */
  onBack: () => void;
  /** Opens the image print page (Patient Detail) for this study, e.g. to print its images again. */
  onOpenImagePrint: (record: PatientStudyRecord) => void;
}

type PrintStatus =
  | { kind: "idle" }
  | { kind: "printing" }
  | { kind: "result"; success: boolean; message: string };

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
 * Report page: opened by clicking a patient in the "Image Printed" tab of
 * Patient List. It has the same layout as the image print page
 * (PatientDetailPage) — a preview on the left, the patient's details and
 * images on the right — but here the preview is the patient's REPORT and the
 * action is "Print Report":
 *
 *  - LEFT: live preview of the report PDF (rebuilt as the fields change) and
 *    the Print Report button.
 *  - RIGHT: patient details, the report's fields to fill in (exam findings,
 *    plus the DICOM auto-filled fields, which can be corrected), and all of
 *    the study's images for reference (click one to enlarge it).
 *
 * All the actual work (auto-fill + merge + PDF render + save + print) is done
 * host-side through the same calls the Report Print page uses.
 */
export default function ReportPreviewPage({ study, onBack, onOpenImagePrint }: ReportPreviewPageProps) {
  const draft = useReportDraft(study);
  const [printStatus, setPrintStatus] = useState<PrintStatus>({ kind: "idle" });
  // Once the report has been printed from this page, printing again is a "reprint".
  const [printedOnce, setPrintedOnce] = useState(false);
  const [enlargedIndex, setEnlargedIndex] = useState<number | null>(null);
  const [brokenThumbnails, setBrokenThumbnails] = useState<Set<string>>(new Set());

  useEffect(() => {
    setPrintStatus({ kind: "idle" });
    setPrintedOnce(false);
    setEnlargedIndex(null);
    setBrokenThumbnails(new Set());
  }, [study?.studyInstanceUid]);

  const images: PatientStudyImage[] = study?.images ?? [];
  const isReprint = Boolean(study?.hasGeneratedReport) || printedOnce;

  // Enlarged image viewer: Esc closes, ← → move between the study's images.
  useEffect(() => {
    if (enlargedIndex === null) {
      return;
    }
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        setEnlargedIndex(null);
      } else if (event.key === "ArrowRight") {
        setEnlargedIndex((index) => (index === null ? null : Math.min(images.length - 1, index + 1)));
      } else if (event.key === "ArrowLeft") {
        setEnlargedIndex((index) => (index === null ? null : Math.max(0, index - 1)));
      }
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [enlargedIndex, images.length]);

  async function handlePrint() {
    if (!study) {
      return;
    }
    setPrintStatus({ kind: "printing" });
    const result = isReprint
      ? await reprintReport(study.studyInstanceUid, draft.payloadFieldValues)
      : await printReport(study.studyInstanceUid, draft.payloadFieldValues);
    if (result.success) {
      setPrintedOnce(true);
    }
    setPrintStatus({
      kind: "result",
      success: result.success,
      message: result.message ?? result.error ?? (result.success ? "Print job sent." : "Print failed.")
    });
  }

  function handleManualFieldChange(field: string, value: string) {
    draft.setManualField(field, value);
    setPrintStatus({ kind: "idle" });
  }

  function handleAutoFieldChange(key: (typeof AUTO_FIELD_KEYS)[number], value: string) {
    draft.setAutoField(key, value);
    setPrintStatus({ kind: "idle" });
  }

  if (!study) {
    return (
      <div className="page">
        <div className="patient-detail__header">
          <h1>Report</h1>
          <button type="button" className="patient-detail__back-button" onClick={onBack}>
            ← Back to Patient List
          </button>
        </div>
        <StateMessage
          icon="📝"
          title="No patient loaded"
          detail="Select a patient in the Image Printed tab of Patient List to open the report here."
        />
      </div>
    );
  }

  const { status, preview, previewLoading, previewError } = draft;
  const enlargedImage = enlargedIndex === null ? null : images[enlargedIndex] ?? null;

  return (
    <div className="page pd-page">
      <div className="pd-workspace">
        {/* LEFT (60%): report preview, starting at the very top */}
        <div className="pd-preview-pane">
          <div className="pd-preview-header">
            <h2 className="patient-detail__pane-title">Report Preview</h2>
            <span className="rp-preview-state">
              {status.kind === "ready" && previewLoading && <Spinner label="Rendering preview..." />}
              {status.kind === "ready" && !previewLoading && previewError && (
                <span className="print-preview__error">⚠️ {previewError}</span>
              )}
              {status.kind === "ready" && !previewLoading && !previewError && preview.url && (
                <span>
                  ✓ Up to date
                  {preview.missingFields && preview.missingFields.length > 0
                    ? ` • ${preview.missingFields.length} field${
                        preview.missingFields.length === 1 ? "" : "s"
                      } left blank`
                    : ""}
                </span>
              )}
            </span>
          </div>

          <div className="rp-preview-frame">
            {preview.url ? (
              <iframe className="rp-preview-frame__iframe" title="Report preview" src={preview.url} />
            ) : (
              <div className="rp-preview-frame__placeholder">
                {status.kind === "loading" || previewLoading ? (
                  <Spinner label="Rendering preview..." />
                ) : (
                  <span>📄 {status.kind === "error" ? "No report template" : "No preview yet"}</span>
                )}
              </div>
            )}
          </div>

          <div className="pd-preview-actions">
            <button
              type="button"
              className="report-print__print-button"
              onClick={handlePrint}
              disabled={status.kind !== "ready" || printStatus.kind === "printing"}
            >
              {printStatus.kind === "printing" ? (
                <Spinner label={isReprint ? "Reprinting..." : "Printing..."} />
              ) : isReprint ? (
                "Reprint Report"
              ) : (
                "Print Report"
              )}
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

        {/* RIGHT (40%): patient info, the report's fields, then all images */}
        <div className="pd-side-pane">
          <div className="pd-side-header">
            <h1>Patient Details</h1>
            <span className="rp-side-links">
              <button type="button" className="patient-detail__back-button" onClick={() => onOpenImagePrint(study)}>
                🖼 Image Print
              </button>
              <button type="button" className="patient-detail__back-button" onClick={onBack}>
                ← Back to Patient List
              </button>
            </span>
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
              <span className="patient-detail__info-value">{studyStatusLabel(study.status)}</span>
            </div>
          </div>

          <section className="rp-form-section">
            <h2 className="pd-images-title">Report Fields</h2>

            {status.kind === "loading" && <LoadingBlock label="Loading report form..." />}

            {status.kind === "error" && (
              <StateMessage
                variant="error"
                icon="⚠️"
                title="Couldn't load the report template"
                detail={`${status.message} A ReportTemplate row must exist for exam type "${study.examType}" — see docs/README.md for how to add one.`}
              />
            )}

            {status.kind === "ready" && (
              <div className="rp-form-scroll">
                {draft.manualFields.length === 0 ? (
                  <p className="report-print__section-hint">This template has no exam-specific fields.</p>
                ) : (
                  <ReportForm
                    fields={draft.manualFields}
                    values={draft.manualValues}
                    onChange={handleManualFieldChange}
                  />
                )}

                <details className="rp-auto-fields">
                  <summary>Auto-filled from DICOM (edit only to correct a mistake)</summary>
                  <div className="report-print__auto-fields">
                    {AUTO_FIELD_KEYS.map((key) => (
                      <label key={key} className="report-print__auto-field">
                        <span className="report-form__label">{AUTO_FIELD_LABELS[key]}</span>
                        <input
                          type="text"
                          value={draft.displayAutoValues[key]}
                          onChange={(event) => handleAutoFieldChange(key, event.target.value)}
                        />
                      </label>
                    ))}
                  </div>
                </details>
              </div>
            )}
          </section>

          <section className="rp-images-section">
            <h2 className="pd-images-title">All Images ({images.length})</h2>
            {images.length === 0 ? (
              <StateMessage icon="🖼️" title="No images in this study" />
            ) : (
              <div className="thumbnail-grid">
                {images.map((image, index) => {
                  const showFallback = !image.thumbnailUrl || brokenThumbnails.has(image.sopInstanceUid);
                  return (
                    <button
                      type="button"
                      key={image.sopInstanceUid}
                      className="thumbnail-grid__item"
                      title="Click to enlarge"
                      onClick={() => setEnlargedIndex(index)}
                    >
                      {showFallback ? (
                        <span className="thumbnail-grid__fallback">No preview</span>
                      ) : (
                        <img
                          src={image.thumbnailUrl}
                          alt=""
                          loading="lazy"
                          onError={() => setBrokenThumbnails((prev) => new Set(prev).add(image.sopInstanceUid))}
                        />
                      )}
                    </button>
                  );
                })}
              </div>
            )}
          </section>
        </div>
      </div>

      {enlargedImage && (
        <div className="rp-lightbox" onClick={() => setEnlargedIndex(null)} role="dialog" aria-modal="true" aria-label="Enlarged image">
          <img
            className="rp-lightbox__image"
            src={enlargedImage.imageUrl ?? enlargedImage.thumbnailUrl}
            alt={`Image ${(enlargedIndex ?? 0) + 1} of ${images.length}`}
            onClick={(event) => event.stopPropagation()}
          />
          <div className="rp-lightbox__bar" onClick={(event) => event.stopPropagation()}>
            <button
              type="button"
              disabled={enlargedIndex === 0}
              onClick={() => setEnlargedIndex((index) => Math.max(0, (index ?? 0) - 1))}
            >
              ‹ Prev
            </button>
            <span>
              {(enlargedIndex ?? 0) + 1} / {images.length}
            </span>
            <button
              type="button"
              disabled={enlargedIndex === images.length - 1}
              onClick={() => setEnlargedIndex((index) => Math.min(images.length - 1, (index ?? 0) + 1))}
            >
              Next ›
            </button>
            <button type="button" onClick={() => setEnlargedIndex(null)}>
              Close
            </button>
          </div>
        </div>
      )}
    </div>
  );
}
