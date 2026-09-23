import { useMemo, useState } from "react";
import { inspectDicomFile, type DicomFieldInfo, type InspectDicomFileResult } from "../ipc";
import StateMessage, { Spinner } from "../components/StateMessage";

type Status =
  | { kind: "idle" }
  | { kind: "loading" }
  | { kind: "result"; result: InspectDicomFileResult };

// Fixed order so "Patient" and "Study / Exam" always appear first, before
// whatever "Measurements" / "Other" tags a given file happens to have.
const CATEGORY_ORDER = ["Patient", "Study / Exam", "Measurements", "Other"];

function groupByCategory(fields: DicomFieldInfo[]): Map<string, DicomFieldInfo[]> {
  const groups = new Map<string, DicomFieldInfo[]>();

  for (const field of fields) {
    const list = groups.get(field.category) ?? [];
    list.push(field);
    groups.set(field.category, list);
  }

  return groups;
}

interface DicomInspectorPageProps {
  /** Returns to the Settings page this tool was opened from. */
  onBack: () => void;
}

/**
 * Lets an operator pick any .dcm file and see every tag it contains —
 * grouped into Patient / Study / Exam / Measurements / Other — including
 * tags that are present but blank for that particular file. Read-only:
 * nothing selected here is imported or written to the database, see
 * ipc.ts's inspectDicomFile / WebViewBridge's HandleInspectDicomFile.
 */
export default function DicomInspectorPage({ onBack }: DicomInspectorPageProps) {
  const [status, setStatus] = useState<Status>({ kind: "idle" });

  async function handleSelectClick() {
    setStatus({ kind: "loading" });
    const result = await inspectDicomFile();
    setStatus({ kind: "result", result });
  }

  const grouped = useMemo(() => {
    if (status.kind !== "result" || !status.result.success) {
      return null;
    }
    return groupByCategory(status.result.fields);
  }, [status]);

  const orderedCategories = useMemo(() => {
    if (!grouped) {
      return [];
    }
    const known = CATEGORY_ORDER.filter((category) => grouped.has(category));
    const extra = [...grouped.keys()].filter((category) => !CATEGORY_ORDER.includes(category));
    return [...known, ...extra];
  }, [grouped]);

  return (
    <div className="page">
      <div className="dicom-inspector__header">
        <h1>DICOM Data Check</h1>
        <button type="button" className="dicom-inspector__back-button" onClick={onBack}>
          ← Back to Settings
        </button>
      </div>
      <p className="dicom-inspector__intro">
        Select any DICOM (.dcm) file to see exactly which fields it carries — including fields
        that exist but are blank for this particular file — and their current values.
      </p>

      <button
        type="button"
        className="dicom-inspector__select-button"
        onClick={handleSelectClick}
        disabled={status.kind === "loading"}
      >
        {status.kind === "loading" ? <Spinner label="Reading file..." /> : "Select DICOM File"}
      </button>

      {status.kind === "result" && status.result.cancelled && (
        <p className="dicom-inspector__message dicom-inspector__message--neutral">No file selected.</p>
      )}

      {status.kind === "result" && !status.result.cancelled && !status.result.success && (
        <StateMessage
          variant="error"
          icon="⚠️"
          title="Couldn't read this file"
          detail={status.result.error ?? "Unknown error."}
        />
      )}

      {status.kind === "result" && status.result.success && grouped && (
        <div className="dicom-inspector__results">
          <p className="dicom-inspector__filename">
            <strong>{status.result.fileName}</strong> — {status.result.fields.length} field(s) found
          </p>

          {orderedCategories.map((category) => (
            <section key={category} className="dicom-inspector__section">
              <h2 className="dicom-inspector__section-title">{category}</h2>
              <table className="dicom-inspector__table">
                <thead>
                  <tr>
                    <th>Field</th>
                    <th>Keyword</th>
                    <th>Tag</th>
                    <th>Value</th>
                  </tr>
                </thead>
                <tbody>
                  {grouped.get(category)!.map((field, index) => (
                    <tr key={`${field.tag}-${field.path}-${index}`}>
                      <td>{field.name}</td>
                      <td className="dicom-inspector__keyword">{field.keyword}</td>
                      <td className="dicom-inspector__tag">{field.tag}</td>
                      <td className={field.value ? "" : "dicom-inspector__empty-value"}>
                        {field.value ? field.value : "(blank)"}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </section>
          ))}
        </div>
      )}
    </div>
  );
}
