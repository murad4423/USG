import { useEffect, useMemo, useState } from "react";
import {
  composeReportPreview,
  getReportFormFields,
  getSavedReportFieldValues,
  type PatientStudyRecord
} from "./ipc";

/**
 * The DICOM-sourced keys ReportRenderService.BuildAutoFilledFields fills in
 * for every report. They are shown (and can be corrected) separately from the
 * exam-specific "manual" fields of the exam type's report template.
 */
export const AUTO_FIELD_KEYS = ["PATIENT_NAME", "PATIENT_ID", "AGE", "SEX", "DATE"] as const;
export type AutoFieldKey = (typeof AUTO_FIELD_KEYS)[number];

export const AUTO_FIELD_LABELS: Record<AutoFieldKey, string> = {
  PATIENT_NAME: "Patient Name",
  PATIENT_ID: "Patient ID",
  AGE: "Age",
  SEX: "Sex",
  DATE: "Exam Date"
};

export type ReportFormStatus =
  | { kind: "loading" }
  | { kind: "ready" }
  | { kind: "error"; message: string };

/**
 * Mirrors ReportRenderService.FormatAge's "34 Y" / derive-from-DOB display,
 * purely so the operator sees the value that will be auto-filled into the
 * PDF. Cosmetic only — the host always computes the value actually printed.
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
 * Everything a report page needs for one study, so the page itself only has
 * to draw it: which exam-specific fields the exam type's template asks for,
 * the values typed so far (resumed from the study's last saved report), the
 * DICOM auto-filled fields (with any corrections), and a debounced, live PDF
 * preview that is recomposed whenever a value changes.
 *
 * The values sent to the host are `payloadFieldValues`: every manual field
 * plus only the auto fields the operator actually changed — untouched auto
 * fields are left to ReportRenderService's own auto-fill.
 */
export function useReportDraft(study: PatientStudyRecord | null) {
  const [status, setStatus] = useState<ReportFormStatus>({ kind: "loading" });
  const [manualFields, setManualFields] = useState<string[]>([]);
  const [manualValues, setManualValues] = useState<Record<string, string>>({});
  const [autoOverrides, setAutoOverrides] = useState<Partial<Record<AutoFieldKey, string>>>({});
  const [preview, setPreview] = useState<{ url?: string; missingFields?: string[]; unusedFields?: string[] }>({});
  const [previewLoading, setPreviewLoading] = useState(false);
  const [previewError, setPreviewError] = useState<string | null>(null);

  const autoDefaults = useMemo<Record<AutoFieldKey, string>>(
    () =>
      study
        ? buildAutoFieldDefaults(study)
        : { PATIENT_NAME: "", PATIENT_ID: "", AGE: "", SEX: "", DATE: "" },
    [study]
  );

  // (Re)load whenever a different study is opened: find out which manual
  // fields this exam type's template needs, then prefill them (and any
  // auto-field corrections) from the study's previously saved report, so
  // re-opening an already reported study resumes instead of starting blank.
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

  /** What the auto-field inputs show: the correction if there is one, otherwise the DICOM-derived value. */
  const displayAutoValues = useMemo<Record<AutoFieldKey, string>>(() => {
    const merged = { ...autoDefaults };
    for (const key of AUTO_FIELD_KEYS) {
      if (autoOverrides[key] !== undefined) {
        merged[key] = autoOverrides[key]!;
      }
    }
    return merged;
  }, [autoDefaults, autoOverrides]);

  const payloadFieldValues = useMemo<Record<string, string>>(
    () => ({ ...manualValues, ...autoOverrides }),
    [manualValues, autoOverrides]
  );

  // Debounced preview recompose whenever any value changes.
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
    // depend on its serialized form so this only re-fires when a value changes.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [study?.studyInstanceUid, status.kind, JSON.stringify(payloadFieldValues)]);

  function setManualField(field: string, value: string) {
    setManualValues((prev) => ({ ...prev, [field]: value }));
  }

  function setAutoField(key: AutoFieldKey, value: string) {
    setAutoOverrides((prev) => {
      const next = { ...prev };
      if (value === autoDefaults[key]) {
        delete next[key];
      } else {
        next[key] = value;
      }
      return next;
    });
  }

  return {
    status,
    manualFields,
    manualValues,
    displayAutoValues,
    payloadFieldValues,
    preview,
    previewLoading,
    previewError,
    setManualField,
    setAutoField
  };
}
