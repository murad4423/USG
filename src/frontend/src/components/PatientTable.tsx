import type { PatientStudyRecord } from "../ipc";
import StateMessage from "./StateMessage";
import { studyProgress } from "../studyStatus";

export type SortKey = "name" | "id" | "date";
export type SortDirection = "asc" | "desc";

export interface SortState {
  key: SortKey;
  direction: SortDirection;
}

interface PatientTableProps {
  records: PatientStudyRecord[];
  sort: SortState;
  onSortChange: (sort: SortState) => void;
  /** Opens the Patient Detail page for this study — its details up top, and its images/print workspace below. */
  onOpenPatient: (record: PatientStudyRecord) => void;
  /** Tooltip shown on a row (what clicking it opens). */
  rowTitle?: string;
}

const SORTABLE_COLUMNS: { key: SortKey; label: string }[] = [
  { key: "name", label: "Name" },
  { key: "id", label: "ID" }
];

/** The three progress circles of a row: image printed, report printed, completed. Grey until that step is done, then green. */
function StatusDots({ status }: { status: string }) {
  const progress = studyProgress(status);
  const steps = [
    { label: "Image printed", done: progress.imagePrinted },
    { label: "Report printed", done: progress.reportPrinted },
    { label: "Completed", done: progress.completed }
  ];

  return (
    <span className="status-dots">
      {steps.map((step) => (
        <span
          key={step.label}
          className={`status-dot${step.done ? " status-dot--on" : ""}`}
          title={`${step.label}: ${step.done ? "yes" : "not yet"}`}
        />
      ))}
    </span>
  );
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
 * One row per study (three progress circles + Name/ID/ExamType/Date-Time),
 * sortable by clicking a column header. The circles show how far the study
 * has come: image printed, report printed, completed (grey = not yet,
 * green = done). Clicking a row navigates to the Patient Detail page for
 * that study, which shows the patient's details up top and a two-column
 * workspace below (all of the study's images on the right, a print
 * preview of the current selection on the left) — see
 * pages/PatientDetailPage.tsx for that page's Print Images / Print Report
 * / Reprint actions.
 */
export default function PatientTable({
  records,
  sort,
  onSortChange,
  onOpenPatient,
  rowTitle = "Click to view this patient's details and images"
}: PatientTableProps) {
  function handleSortClick(key: SortKey) {
    if (sort.key === key) {
      onSortChange({ key, direction: sort.direction === "asc" ? "desc" : "asc" });
    } else {
      // Date defaults to newest-first on first click; name/ID default A-Z.
      onSortChange({ key, direction: key === "date" ? "desc" : "asc" });
    }
  }

  if (records.length === 0) {
    return (
      <StateMessage icon="🗂️" title="No patients match the current search/filters" />
    );
  }

  return (
    <table className="patient-table">
      <thead>
        <tr>
          <th className="patient-table__status-col" aria-hidden="true" />
          {SORTABLE_COLUMNS.map((column) => (
            <th key={column.key}>
              <button
                type="button"
                className="patient-table__sort-button"
                onClick={() => handleSortClick(column.key)}
              >
                {column.label}
                {sort.key === column.key && (
                  <span className="patient-table__sort-arrow">{sort.direction === "asc" ? " ▲" : " ▼"}</span>
                )}
              </button>
            </th>
          ))}
          <th>Exam Type</th>
          <th>
            <button
              type="button"
              className="patient-table__sort-button"
              onClick={() => handleSortClick("date")}
            >
              Date / Time
              {sort.key === "date" && (
                <span className="patient-table__sort-arrow">{sort.direction === "asc" ? " ▲" : " ▼"}</span>
              )}
            </button>
          </th>
        </tr>
      </thead>
      <tbody>
        {records.map((record) => (
          <tr
            key={record.studyInstanceUid}
            className="patient-table__row"
            onClick={() => onOpenPatient(record)}
            title={rowTitle}
          >
            <td className="patient-table__status-col">
              <StatusDots status={record.status} />
            </td>
            <td>{record.patientName}</td>
            <td>{record.patientId}</td>
            <td>{record.examType}</td>
            <td>{formatDateTime(record.studyDateTime)}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}
