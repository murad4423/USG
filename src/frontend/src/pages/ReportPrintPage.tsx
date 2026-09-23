import { useEffect, useMemo, useState } from "react";
import ReportForm from "../components/ReportForm";
import StateMessage, { LoadingBlock, Spinner } from "../components/StateMessage";
import {
  getReportFormFields,
  getSavedReportFieldValues,
  composeReportPreview,
  printReport,
  reprintReport,
  type PatientStudyRecord
} from "../ipc";

interface ReportPrintPageProps {
  /** The study to generate a report for, loaded via PatientListPage's "Print Report" action. Null until one has been chosen. */
  study: PatientStudyRecord | null;
  /**
   * "generate" (default): the normal flow — the Print button calls
   * `printReport`, always (re)generating the PDF.
   * "reprint": reached via PatientListPage's "Edit & Reprint" action for a
   * study that already has a saved report. The form still starts from the
   * same saved field values (no behavior change there — this page has
   * always resumed a previous draft), but the Print button becomes
   * "Reprint Report" and calls `reprintReport` with whatever is currently
   * in the form, so an edit here is logged as a reprint, not an original
   * print, and everything still comes from local data — no DICOM re-receive.
   */
  mode?: "generate" | "reprint";
}

// The DICOM-sourced keys ReportRenderService.BuildAutoFilledFields auto-fills
// for every report. Kept here just to know which keys are shown/edited in
// the "Auto-filled from DICOM" section rather than rendered by ReportForm.
const AUTO_FIELD_KEYS = ["PATIENT_NAME", "PATIENT_ID", "AGE", "SEX", "DATE"] as const;
type AutoFieldKey = (typeof AUTO_FIELD_KEYS)[number];

const AUTO_FIELD_LABELS: Record<AutoFieldKey, string> = {
  PATIENT_NAME: "Patient Name",
  PATIENT_ID: "Patient ID",
  AGE: "Age",
  SEX: "Sex",
  DATE: "Exam Date"
};

type FormStatus =
  | { kind: "loading" }
  | { kind: "ready" }
  | { kind: "error"; message: string };

type PrintStatus =
  | { kind: "idle" }
  | { kind: "printing" }
  | { kind: "result"; success: boolean; message: string };

/**
 * Mirrors ReportRenderService.FormatAge's "34 Y" / derive-from-DOB display,
 * purely so the operator sees the same value that'll be auto-filled into
 * the PDF. Cosmetic only — the server-side ReportRenderService call is
 * always what actually computes/writes the value used in the generated
 * report; this is never sent unless the operator edits it (see
 * `autoOverrides` below).
 */
function formatDisplayAge(study: PatientStudyRecord): string {
  if (typeof study.age === "number") {
    return `${study.age} Y`;
  }
  if (study.dateOfBirth) {
    const dob = new Date(study.dateOfBirth);
    if (!Number.isNaN(dob.getTime())) {
      const today = new Date();
      let years = today.getFullYear() - dob.getFullYear();
      const hadBirthdayThisYear =
        today.getMonth() > dob.getMonth() ||
        (today.getMonth() === dob.getMonth() && today.getDate() >= dob.getDate());
      if (!hadBirthdayThisYear) {
        years -= 1;
      }
      return `${years} Y`;
    }
  }
  return "";
}

function formatDisplayDate(study: PatientStudyRecord): string {
  const date = new Date(study.studyDateTime);
  if (Number.isNaN(date.getTime())) {
    return study.studyDateTime;
  }
  return date.toLocaleDateString(undefined, { day: "2-digit", month: "short", year: "numeric" });
}

function buildAutoFieldDefaults(study: PatientStudyRecord): Record<AutoFieldKey, string> {
  return {
    PATIENT_NAME: study.patientName,
    PATIENT_ID: study.patientId,
    AGE: formatDisplayAge(study),
    SEX: study.sex ?? "",
    DATE: formatDisplayDate(study)
  };
}

/**
 * Report Print page: shows the DICOM auto-filled fields (editable, in case
 * a mistake needs correcting), the exam type's exam-specific ReportForm,
 * a live print-preview of the merged PDF, and a Print button. All actual
 * generation (auto-fill + merge + PDF render + save) happens host-side via
 * ReportRenderService (see ipc.ts's composeReportPreview/printReport) —
 * this page only collects field values and displays what comes back.
 */
export default function ReportPrintPage({ study, mode = "generate" }: ReportPrintPageProps) {
  const [status, setStatus] = useState<FormStatus>({ kind: "loading" });
  const [manualFields, setManualFields] = useState<string[]>([]);
  const [manualValues, setManualValues] = useState<Record<string, string>>({});
  // Only populated for an auto-field the operator has actually edited away
  // from its DICOM-derived default — left untouched, that field is omitted
  // from what's sent to the host so ReportRenderService's own auto-fill
  // (the authoritative source) is used instead of this page's display copy.
  const [autoOverrides, setAutoOverrides] = useState<Partial<Record<AutoFieldKey, string>>>({});
  const [preview, setPreview] = useState<{
    url?: string;
    missingFields?: string[];
    unusedFields?: string[];
  }>({});
  const [previewLoading, setPreviewLoading] = useState(false);
  const [previewError, setPreviewError] = useState<string | null>(null);
  const [printStatus, setPrintStatus] = useState<PrintStatus>({ kind: "idle" });

  const autoDefaults = useMemo<Record<AutoFieldKey, string>>(
    () =>
      study
        ? buildAutoFieldDefaults(study)
        : { PATIENT_NAME: "", PATIENT_ID: "", AGE: "", SEX: "", DATE: "" },
    [study]
  );

  // (Re)load the form whenever a different study is opened: find out which
  // manual fields this exam type's template needs, then prefill both the
  // manual fields and any auto-field overrides from a previously saved
  // report for this study (if one exists), so re-opening an already
  // reported study resumes instead of starting blank.
  useEffect(() => {
    if (!study) {
      return;
    }

    let cancelled = false;
    setStatus({ kind: "loading" });
    setManualFields([]);
    setManualValues({});
    setAutoOverrides({});
    setPreview({});
    setPreviewError(null);
    setPrintStatus({ kind: "idle" });

    const defaults = buildAutoFieldDefaults(study);

    (async () => {
      const fieldsResult = await getReportFormFields(study.examType);
      if (cancelled) {
        return;
      }

      if (!fieldsResult.success) {
        setStatus({
          kind: "error",
          message: fieldsResult.error ?? "Could not load the report template for this exam type."
        });
        return;
      }

      const fields = fieldsResult.manualFields ?? [];
      setManualFields(fields);

      const savedResult = await getSavedReportFieldValues(study.studyInstanceUid);
      if (cancelled) {
        return;
      }

      const saved = savedResult.success ? savedResult.fieldValues ?? {} : {};

      const initialManualValues: Record<string, string> = {};
      for (const field of fields) {
        initialManualValues[field] = saved[field] ?? "";
      }
      setManualValues(initialManualValues);

      const initialOverrides: Partial<Record<AutoFieldKey, string>> = {};
      for (const key of AUTO_FIELD_KEYS) {
        const savedValue = saved[key];
        if (savedValue !== undefined && savedValue !== defaults[key]) {
          initialOverrides[key] = savedValue;
        }
      }
      setAutoOverrides(initialOverrides);

      setStatus({ kind: "ready" });
    })();

    return () => {
      cancelled = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [study?.studyInstanceUid]);

  // What the auto-field inputs display: the override if the operator
  // edited one, otherwise the DICOM-derived default.
  const displayAutoValues = useMemo<Record<AutoFieldKey, string>>(() => {
    const merged = { ...autoDefaults };
    for (const key of AUTO_FIELD_KEYS) {
      if (autoOverrides[key] !== undefined) {
        merged[key] = autoOverrides[key]!;
      }
    }
    return merged;
  }, [autoDefaults, autoOverrides]);

  // What's actually sent to the host: every manual field, plus only the
  // auto fields the operator overrode.
  const payloadFieldValues = useMemo<Record<string, string>>(
    () => ({ ...manualValues, ...autoOverrides }),
    [manualValues, autoOverrides]
  );

  // Debounced preview recompose whenever any field value changes.
  useEffect(() => {
    if (!study || status.kind !== "ready") {
      return;
    }

    let cancelled = false;
    setPreviewLoading(true);
    setPreviewError(null);

    const timer = setTimeout(async () => {
      const result = await composeReportPreview(study.studyInstanceUid, payloadFieldValues);
      if (cancelled) {
        return;
      }

      if (result.success) {
        setPreview({ url: result.previewUrl, missingFields: result.missingFields, unusedFields: result.unusedFields });
        setPreviewError(null);
      } else {
        setPreview({});
        setPreviewError(result.error ?? "Failed to build report preview.");
      }
      setPreviewLoading(false);
    }, 400);

    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
    // payloadFieldValues is a derived object (new identity every render) —
    // depend on its serialized form so this only re-fires when a value
    // actually changes, the same debounce pattern ImagePrintPage uses.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [study?.studyInstanceUid, status.kind, JSON.stringify(payloadFieldValues)]);

  function handleManualFieldChange(field: string, value: string) {
    setManualValues((prev) => ({ ...prev, [field]: value }));
    setPrintStatus({ kind: "idle" });
  }

  function handleAutoFieldChange(key: AutoFieldKey, value: string) {
    setAutoOverrides((prev) => {
      const next = { ...prev };
      if (value === autoDefaults[key]) {
        delete next[key];
      } else {
        next[key] = value;
      }
      return next;
    });
    setPrintStatus({ kind: "idle" });
  }

  async function handlePrint() {
    if (!study) {
      return;
    }
    setPrintStatus({ kind: "printing" });
    const result =
      mode === "reprint"
        ? await reprintReport(study.studyInstanceUid, payloadFieldValues)
        : await printReport(study.studyInstanceUid, payloadFieldValues);
    setPrintStatus({
      kind: "result",
      success: result.success,
      message: result.message ?? result.error ?? (result.success ? "Print job sent." : "Print failed.")
    });
  }

  if (!study) {
    return (
      <div className="page">
        <h1>Report Print</h1>
        <StateMessage
          icon="📝"
          title="No study loaded"
          detail="Select a patient's study from Patient List and choose &quot;Print Report&quot; to load it here."
        />
      </div>
    );
  }

  return (
    <div className="page">
      <h1>{mode === "reprint" ? "Reprint Report" : "Report Print"}</h1>
      <p className="report-print__subject">
        {study.patientName} ({study.patientId}) — {study.examType}
      </p>

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
        <>
          <section className="report-print__section">
            <h2>Auto-filled from DICOM</h2>
            <p className="report-print__section-hint">
              Sourced from the stored patient/study record. Edit only to correct a mistake — the
              report uses whatever you leave here.
            </p>
            <div className="report-print__auto-fields">
              {AUTO_FIELD_KEYS.map((key) => (
                <label key={key} className="report-print__auto-field">
                  <span className="report-form__label">{AUTO_FIELD_LABELS[key]}</span>
                  <input
                    type="text"
                    value={displayAutoValues[key]}
                    onChange={(event) => handleAutoFieldChange(key, event.target.value)}
                  />
                </label>
              ))}
            </div>
          </section>

          <section className="report-print__section">
            <h2>Exam Findings</h2>
            {manualFields.length === 0 ? (
              <p className="report-print__section-hint">This template has no exam-specific fields.</p>
            ) : (
              <ReportForm fields={manualFields} values={manualValues} onChange={handleManualFieldChange} />
            )}
          </section>

          <section className="report-print__section">
            <h2>Preview</h2>
            <div className="print-preview">
              <div className="print-preview__summary">
                {previewLoading && <Spinner label="Rendering preview..." />}
                {!previewLoading && previewError && <span className="print-preview__error">⚠️ {previewError}</span>}
                {!previewLoading && !previewError && preview.url && (
                  <span>
                    ✓ Preview up to date
                    {preview.missingFields && preview.missingFields.length > 0
                      ? ` • ${preview.missingFields.length} field${
                          preview.missingFields.length === 1 ? "" : "s"
                        } left blank (${preview.missingFields.join(", ")})`
                      : ""}
                  </span>
                )}
                {!previewLoading && !previewError && !preview.url && <span>Preview will appear here.</span>}
              </div>

              <div className="print-preview__frame-wrapper">
                {preview.url ? (
                  <iframe className="print-preview__frame" title="Report preview" src={preview.url} />
                ) : (
                  <div className="print-preview__placeholder">
                    {previewLoading ? <Spinner label="Rendering preview..." /> : <span>📄 No preview yet</span>}
                  </div>
                )}
              </div>
            </div>
          </section>

          <div className="report-print__actions">
            <button
              type="button"
              className="report-print__print-button"
              onClick={handlePrint}
              disabled={printStatus.kind === "printing"}
            >
              {printStatus.kind === "printing"
                ? <Spinner label={mode === "reprint" ? "Reprinting..." : "Printing..."} />
                : mode === "reprint"
                  ? "Reprint Report"
                  : "Print Report"}
            </button>

            {printStatus.kind === "result" && (
              <p
                className={`report-print__print-status report-print__print-status--${
                  printStatus.success ? "success" : "error"
                }`}
              >
                {printStatus.success ? "✓" : "⚠️"} {printStatus.message}
              </p>
            )}
          </div>
        </>
      )}
    </div>
  );
}
