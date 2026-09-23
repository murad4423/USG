interface ReportFormProps {
  /**
   * Exam-specific placeholder field keys from the exam type's report
   * template (e.g. "LIVER_SIZE", "IMPRESSION") — everything the operator
   * needs to type in beyond the DICOM auto-filled fields (Patient
   * Name/ID/Age/Sex/Date), which ReportPrintPage renders separately.
   */
  fields: string[];
  /** Current value per field key (controlled inputs). */
  values: Record<string, string>;
  onChange: (field: string, value: string) => void;
  disabled?: boolean;
}

// A handful of well-known ultrasound-report abbreviations that should stay
// upper-case in labels instead of being title-cased like a normal word.
const KNOWN_ACRONYMS = new Set([
  "CBD",
  "BPD",
  "HC",
  "AC",
  "FL",
  "EFW",
  "AFI",
  "LMP",
  "EDD"
]);

// Fields free-text/long enough to deserve a multi-line textarea instead of
// a single-line input — matched by substring so e.g. both "IMPRESSION" and
// a hypothetical "CLINICAL_IMPRESSION" get one.
const LONG_TEXT_MARKERS = ["IMPRESSION", "FINDING", "NOTE", "HISTORY", "COMMENT"];

/** "LIVER_SIZE" -> "Liver Size"; "CBD" -> "CBD" (known acronym kept upper-case). */
function toLabel(field: string): string {
  return field
    .split("_")
    .filter(Boolean)
    .map((word) => (KNOWN_ACRONYMS.has(word) ? word : word.charAt(0) + word.slice(1).toLowerCase()))
    .join(" ");
}

function isLongTextField(field: string): boolean {
  return LONG_TEXT_MARKERS.some((marker) => field.includes(marker));
}

/**
 * Renders one labeled input (or textarea, for free-text fields like
 * IMPRESSION) per entry in `fields`. Purely presentational — every field
 * it shows comes from the backend's introspection of the exam type's HTML
 * template (see ipc.ts's getReportFormFields), so this component never
 * hardcodes which fields belong to which exam type and can't drift out of
 * sync with the template ReportRenderService actually merges into.
 */
export default function ReportForm({ fields, values, onChange, disabled }: ReportFormProps) {
  if (fields.length === 0) {
    return null;
  }

  return (
    <div className="report-form">
      {fields.map((field) => (
        <label key={field} className="report-form__field">
          <span className="report-form__label">{toLabel(field)}</span>
          {isLongTextField(field) ? (
            <textarea
              className="report-form__textarea"
              rows={4}
              value={values[field] ?? ""}
              onChange={(event) => onChange(field, event.target.value)}
              disabled={disabled}
            />
          ) : (
            <input
              type="text"
              className="report-form__input"
              value={values[field] ?? ""}
              onChange={(event) => onChange(field, event.target.value)}
              disabled={disabled}
            />
          )}
        </label>
      ))}
    </div>
  );
}
