import { useEffect, useMemo, useRef, useState } from "react";
import DicomUploadControl from "../components/DicomUploadControl";
import SearchBar from "../components/SearchBar";
import DateRangeFilter, { type DateRangeValue } from "../components/DateRangeFilter";
import PatientTable, { type SortState } from "../components/PatientTable";
import StudyTabs, { type StudyTabKey } from "../components/StudyTabs";
import StateMessage, { LoadingBlock, Spinner } from "../components/StateMessage";
import { studyBelongsToTab } from "../studyStatus";
import {
  listPatientStudies,
  searchPatientStudies,
  filterPatientStudiesByDateRange,
  type PatientStudyQueryResult,
  type PatientStudyRecord
} from "../ipc";
import { subscribeHostEvent } from "../ipcLive";

/** Fallback refresh, in case a live "studiesChanged" push is ever missed. */
const SAFETY_REFRESH_MS = 30000;
/** A request the host never answers must not block the ones after it. */
const REQUEST_TIMEOUT_MS = 10000;

// The host's replies to list/search/filter carry no request id, so two requests in flight at once could be
// answered with each other's data. Running them strictly one after another avoids that (a live refresh can
// easily overlap with a search or a manual refresh).
let requestQueue: Promise<unknown> = Promise.resolve();

function runExclusive<T>(task: () => Promise<T>, onTimeout: T): Promise<T> {
  const run = requestQueue.then(
    () =>
      new Promise<T>((resolve) => {
        const timer = window.setTimeout(() => resolve(onTimeout), REQUEST_TIMEOUT_MS);
        task().then(
          (value) => {
            window.clearTimeout(timer);
            resolve(value);
          },
          () => {
            window.clearTimeout(timer);
            resolve(onTimeout);
          }
        );
      })
  );
  requestQueue = run;
  return run;
}

/** Mirrors PatientStudyService's inclusive, whole-day date range semantics on the client. */
function matchesDateRange(studyDateTimeIso: string, range: DateRangeValue): boolean {
  const studyDate = new Date(studyDateTimeIso);
  if (Number.isNaN(studyDate.getTime())) {
    return true; // Don't hide a row we can't parse the date of.
  }

  if (range.startDate) {
    const start = new Date(`${range.startDate}T00:00:00`);
    if (studyDate < start) {
      return false;
    }
  }

  if (range.endDate) {
    const endExclusive = new Date(`${range.endDate}T00:00:00`);
    endExclusive.setDate(endExclusive.getDate() + 1);
    if (studyDate >= endExclusive) {
      return false;
    }
  }

  return true;
}

interface PatientListPageProps {
  /** Opens the Patient Detail (image print) page for the clicked study — used by the "Waiting Study" tab. */
  onOpenPatient: (record: PatientStudyRecord) => void;
  /** Opens the Report page for the clicked study — used by the "Image Printed" tab. */
  onOpenReport: (record: PatientStudyRecord) => void;
  /** The selected tab. Kept by App so coming back from a patient's page returns to the same tab. */
  activeTab: StudyTabKey;
  onTabChange: (tab: StudyTabKey) => void;
}

export default function PatientListPage({ onOpenPatient, onOpenReport, activeTab, onTabChange }: PatientListPageProps) {
  const [searchText, setSearchText] = useState("");
  const [dateRange, setDateRange] = useState<DateRangeValue>({ startDate: "", endDate: "" });
  const [sort, setSort] = useState<SortState>({ key: "date", direction: "desc" });
  // "Waiting Study" (images not printed yet) and "Image Printed" are
  // functional; Report Printed and Completed are visible but inert for now
  // (see StudyTabs) — `tabRecords` below filters the list by `activeTab`.

  const [records, setRecords] = useState<PatientStudyRecord[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [reloadToken, setReloadToken] = useState(0);
  // true = the next reload was triggered by a live update: refresh the rows quietly, with no spinner / error flash.
  const silentReloadRef = useRef(false);

  // Live updates: reload the moment the host says a study arrived from the machine, when the window comes
  // back into view, and (as a safety net) every SAFETY_REFRESH_MS.
  useEffect(() => {
    function requestSilentReload() {
      silentReloadRef.current = true;
      setReloadToken((t) => t + 1);
    }

    const unsubscribe = subscribeHostEvent("studiesChanged", requestSilentReload);
    const timer = window.setInterval(() => {
      if (document.visibilityState === "visible") {
        requestSilentReload();
      }
    }, SAFETY_REFRESH_MS);
    function handleVisibility() {
      if (document.visibilityState === "visible") {
        requestSilentReload();
      }
    }
    document.addEventListener("visibilitychange", handleVisibility);

    return () => {
      unsubscribe();
      window.clearInterval(timer);
      document.removeEventListener("visibilitychange", handleVisibility);
    };
  }, []);

  // Debounced fetch whenever the search text, date range, or an explicit
  // reload (Refresh button / a completed upload) changes. The debounce is
  // short (150ms) since this is a local IPC round-trip, not a network
  // call — it just avoids firing on every single keystroke.
  useEffect(() => {
    let cancelled = false;
    const silent = silentReloadRef.current;
    silentReloadRef.current = false;

    const timer = setTimeout(async () => {
      if (!silent) {
        setLoading(true);
        setError(null);
      }

      const trimmedSearch = searchText.trim();
      const { startDate, endDate } = dateRange;

      // Use whichever IPC method fits: search takes priority (the date
      // range is then re-applied client-side below, since there's no
      // combined search+date-range endpoint), otherwise fall back to the
      // date-range endpoint, otherwise the plain list.
      const failed: PatientStudyQueryResult = {
        success: false,
        error: "The app did not answer in time.",
        records: []
      };
      const result = await runExclusive<PatientStudyQueryResult>(() => {
        if (cancelled) {
          return Promise.resolve({ success: true, records: [] });
        }
        return trimmedSearch
          ? searchPatientStudies(trimmedSearch)
          : startDate || endDate
            ? filterPatientStudiesByDateRange(startDate || undefined, endDate || undefined)
            : listPatientStudies();
      }, failed);

      if (cancelled) {
        return;
      }

      if (result.success) {
        setRecords(result.records);
        setError(null);
      } else if (!silent) {
        setError(result.error ?? "Failed to load patients.");
        setRecords([]);
      }
      // A failed quiet refresh keeps the rows already on screen.
      setLoading(false);
    }, 150);

    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
  }, [searchText, dateRange, reloadToken]);

  // Always re-applied client-side: harmless when the server already
  // filtered by date, and necessary when the search endpoint was used
  // instead (it doesn't consider dates), so search + date range combine
  // correctly.
  const visibleRecords = useMemo(
    () => records.filter((record) => matchesDateRange(record.studyDateTime, dateRange)),
    [records, dateRange]
  );

  const sortedRecords = useMemo(() => {
    const sorted = [...visibleRecords];
    sorted.sort((a, b) => {
      let comparison = 0;
      if (sort.key === "name") {
        comparison = a.patientName.localeCompare(b.patientName);
      } else if (sort.key === "id") {
        comparison = a.patientId.localeCompare(b.patientId, undefined, { numeric: true });
      } else {
        comparison = new Date(a.studyDateTime).getTime() - new Date(b.studyDateTime).getTime();
      }
      return sort.direction === "asc" ? comparison : -comparison;
    });
    return sorted;
  }, [visibleRecords, sort]);

  // The rows of the active tab: a patient sits in "Waiting Study" until
  // their images are printed, then moves to "Image Printed".
  const tabRecords = useMemo(
    () => sortedRecords.filter((record) => studyBelongsToTab(record.status, activeTab)),
    [sortedRecords, activeTab]
  );

  const hasFilters = Boolean(searchText || dateRange.startDate || dateRange.endDate);

  return (
    <div className="page">
      <h1>Patient List</h1>

      <DicomUploadControl
        onUploadComplete={(result) => {
          if (result.success) {
            setReloadToken((t) => t + 1);
          }
        }}
      />

      <div className="patient-list__toolbar">
        <SearchBar value={searchText} onChange={setSearchText} />
        <DateRangeFilter value={dateRange} onChange={setDateRange} />
        <button
          type="button"
          className="patient-list__refresh"
          onClick={() => setReloadToken((t) => t + 1)}
          disabled={loading}
        >
          {loading ? <Spinner label="Refreshing..." /> : "Refresh"}
        </button>
      </div>

      <StudyTabs active={activeTab} onChange={onTabChange} />

      {error && (
        <StateMessage
          variant="error"
          icon="⚠️"
          title="Couldn't load patients"
          detail={error}
          action={{ label: "Retry", onClick: () => setReloadToken((t) => t + 1) }}
        />
      )}

      {loading && records.length === 0 && !error ? (
        <LoadingBlock label="Loading patients..." />
      ) : !error && tabRecords.length === 0 ? (
        <StateMessage
          icon="🗂️"
          title={
            records.length === 0 && !hasFilters
              ? "No patients yet"
              : sortedRecords.length > 0
                ? activeTab === "imagePrinted"
                  ? "No image-printed studies yet"
                  : "No waiting studies"
                : "No patients match the current search/filters"
          }
          detail={
            records.length === 0 && !hasFilters
              ? "Use \"Manual DICOM Upload\" above to bring in a study, or connect the ultrasound machine so studies arrive automatically."
              : sortedRecords.length > 0
                ? activeTab === "imagePrinted"
                  ? "A patient moves here once their images have been printed."
                  : "Every study in the current list has already had its images printed."
                : "Try clearing the search text or widening the date range."
          }
        />
      ) : (
        <PatientTable
          records={tabRecords}
          sort={sort}
          onSortChange={setSort}
          // A waiting patient opens the image print page; a patient whose images
          // are already printed opens the report page.
          onOpenPatient={activeTab === "imagePrinted" ? onOpenReport : onOpenPatient}
          rowTitle={
            activeTab === "imagePrinted"
              ? "Click to open this patient's report"
              : "Click to view this patient's details and images"
          }
        />
      )}
    </div>
  );
}
