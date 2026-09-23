import { useEffect, useRef, useState } from "react";
import type { PatientStudyRecord } from "./ipc";
import PatientListPage from "./pages/PatientListPage";
import PatientDetailPage from "./pages/PatientDetailPage";
import ReportPreviewPage from "./pages/ReportPreviewPage";
import type { StudyTabKey } from "./components/StudyTabs";
import ReportPrintPage from "./pages/ReportPrintPage";
import SettingsPage from "./pages/SettingsPage";
import DicomInspectorPage from "./pages/DicomInspectorPage";
import ReportTemplateBuildPage from "./pages/ReportTemplateBuildPage";
import ConnectionStatusIndicators from "./components/ConnectionStatusIndicators";
import { useConnectionStatus } from "./useConnectionStatus";

// "Image Print" is no longer a standalone nav destination — its
// compose/select/print workflow now lives inside the Patient Detail page
// (opened by clicking a row in Patient List), so a study is always in
// hand before that workflow is shown.
// "report-preview" is the Report page (same layout as Patient Detail, but the
// preview is the patient's report) opened from the "Image Printed" tab.
type PageKey =
  | "patients"
  | "patient-detail"
  | "report-preview"
  | "report-print"
  | "settings"
  | "dicom-inspector"
  | "report-template-build";

const NAV_ITEMS: { key: PageKey; label: string }[] = [
  { key: "patients", label: "Patient List" },
  { key: "report-print", label: "Report Print" },
  { key: "settings", label: "Settings" }
];

/** How long the menu stays open after the mouse has left it, before it hides itself. */
const NAV_AUTO_HIDE_MS = 700;

interface RenderPageArgs {
  page: PageKey;
  patientListTab: StudyTabKey;
  onPatientListTabChange: (tab: StudyTabKey) => void;
  patientDetailStudy: PatientStudyRecord | null;
  reportPreviewStudy: PatientStudyRecord | null;
  onOpenReport: (record: PatientStudyRecord) => void;
  onBackFromReportPreview: () => void;
  reportPrintStudy: PatientStudyRecord | null;
  reportPrintMode: "generate" | "reprint";
  onOpenPatient: (record: PatientStudyRecord) => void;
  onBackFromPatientDetail: () => void;
  onPrintReport: (record: PatientStudyRecord) => void;
  onEditAndReprintReport: (record: PatientStudyRecord) => void;
  onOpenDicomInspector: () => void;
  onBackFromDicomInspector: () => void;
  onOpenReportTemplateBuilder: () => void;
  onBackFromReportTemplateBuilder: () => void;
}

function renderPage({
  page,
  patientListTab,
  onPatientListTabChange,
  patientDetailStudy,
  reportPreviewStudy,
  onOpenReport,
  onBackFromReportPreview,
  reportPrintStudy,
  reportPrintMode,
  onOpenPatient,
  onBackFromPatientDetail,
  onPrintReport,
  onEditAndReprintReport,
  onOpenDicomInspector,
  onBackFromDicomInspector,
  onOpenReportTemplateBuilder,
  onBackFromReportTemplateBuilder
}: RenderPageArgs) {
  switch (page) {
    case "patients":
      return (
        <PatientListPage
          onOpenPatient={onOpenPatient}
          onOpenReport={onOpenReport}
          activeTab={patientListTab}
          onTabChange={onPatientListTabChange}
        />
      );
    case "patient-detail":
      return (
        <PatientDetailPage
          study={patientDetailStudy}
          onBack={onBackFromPatientDetail}
          onPrintReport={onPrintReport}
          onEditAndReprintReport={onEditAndReprintReport}
        />
      );
    case "report-preview":
      return (
        <ReportPreviewPage
          study={reportPreviewStudy}
          onBack={onBackFromReportPreview}
          onOpenImagePrint={onOpenPatient}
        />
      );
    case "report-print":
      return <ReportPrintPage study={reportPrintStudy} mode={reportPrintMode} />;
    case "settings":
      return (
        <SettingsPage
          onOpenDicomInspector={onOpenDicomInspector}
          onOpenReportTemplateBuilder={onOpenReportTemplateBuilder}
        />
      );
    case "dicom-inspector":
      return <DicomInspectorPage onBack={onBackFromDicomInspector} />;
    case "report-template-build":
      return <ReportTemplateBuildPage onBack={onBackFromReportTemplateBuilder} />;
  }
}

export default function App() {
  const [activePage, setActivePage] = useState<PageKey>("patients");
  // Live status: the native bridge and the ultrasound machine, re-checked continuously (see useConnectionStatus).
  const { bridgeStatus, machine } = useConnectionStatus();
  // The menu is hidden by default: only a slim strip with the menu icon is
  // always visible. Clicking the icon slides the menu out over the page; it
  // hides itself again when a page is chosen, when the operator clicks
  // elsewhere, presses Esc, or moves the mouse away from it.
  const [navOpen, setNavOpen] = useState(false);
  const navHideTimer = useRef<number | null>(null);

  function cancelNavHide() {
    if (navHideTimer.current !== null) {
      window.clearTimeout(navHideTimer.current);
      navHideTimer.current = null;
    }
  }

  function scheduleNavHide() {
    cancelNavHide();
    navHideTimer.current = window.setTimeout(() => setNavOpen(false), NAV_AUTO_HIDE_MS);
  }

  function handleNavItemClick(page: PageKey) {
    setActivePage(page);
    cancelNavHide();
    setNavOpen(false);
  }

  useEffect(() => {
    if (!navOpen) {
      cancelNavHide();
      return;
    }
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        setNavOpen(false);
      }
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [navOpen]);

  useEffect(() => cancelNavHide, []);
  // The study Patient Detail should show, set by PatientTable's row click.
  // Lives here (rather than in PatientDetailPage itself) so it survives
  // switching away to another nav item and back.
  const [patientDetailStudy, setPatientDetailStudy] = useState<PatientStudyRecord | null>(null);
  // The study the Report page (opened from the "Image Printed" tab) shows.
  const [reportPreviewStudy, setReportPreviewStudy] = useState<PatientStudyRecord | null>(null);
  // Which Patient List tab is selected. Lives here so that coming back from a
  // patient's page returns to the same tab (the list page itself is re-created).
  const [patientListTab, setPatientListTab] = useState<StudyTabKey>("waiting");
  // Same idea for Report Print, set by PatientTable's "Print Report" /
  // "Edit & Reprint" buttons. reportPrintMode tracks which one was used,
  // so ReportPrintPage knows whether its Print button should generate a
  // fresh report (printReport) or reprint from stored data (reprintReport).
  const [reportPrintStudy, setReportPrintStudy] = useState<PatientStudyRecord | null>(null);
  const [reportPrintMode, setReportPrintMode] = useState<"generate" | "reprint">("generate");

  function handleOpenPatient(record: PatientStudyRecord) {
    setPatientDetailStudy(record);
    setActivePage("patient-detail");
  }

  function handleBackFromPatientDetail() {
    setActivePage("patients");
  }

  function handleOpenReport(record: PatientStudyRecord) {
    setReportPreviewStudy(record);
    setActivePage("report-preview");
  }

  function handleBackFromReportPreview() {
    setActivePage("patients");
  }

  function handlePrintReport(record: PatientStudyRecord) {
    setReportPrintStudy(record);
    setReportPrintMode("generate");
    setActivePage("report-print");
  }

  function handleEditAndReprintReport(record: PatientStudyRecord) {
    setReportPrintStudy(record);
    setReportPrintMode("reprint");
    setActivePage("report-print");
  }

  function handleOpenDicomInspector() {
    setActivePage("dicom-inspector");
  }

  function handleBackFromDicomInspector() {
    setActivePage("settings");
  }

  function handleOpenReportTemplateBuilder() {
    setActivePage("report-template-build");
  }

  function handleBackFromReportTemplateBuilder() {
    setActivePage("settings");
  }

  return (
    <div className="app-shell">
      {/* Always-visible slim strip: the menu icon on top, the bridge status dot at the bottom. */}
      <div className="app-rail" onMouseEnter={cancelNavHide} onMouseLeave={() => navOpen && scheduleNavHide()}>
        <button
          type="button"
          className="app-rail__menu-button"
          aria-label={navOpen ? "Hide menu" : "Show menu"}
          aria-expanded={navOpen}
          aria-controls="app-nav"
          title={navOpen ? "Hide menu" : "Menu"}
          onClick={() => {
            cancelNavHide();
            setNavOpen((open) => !open);
          }}
        >
          <svg width="20" height="20" viewBox="0 0 20 20" aria-hidden="true">
            <path d="M3 5h14M3 10h14M3 15h14" stroke="currentColor" strokeWidth="2" strokeLinecap="round" />
          </svg>
        </button>
        <ConnectionStatusIndicators variant="rail" bridge={bridgeStatus} machine={machine} />
      </div>

      {navOpen && <div className="app-nav-backdrop" onClick={() => setNavOpen(false)} />}

      <nav
        id="app-nav"
        className={`app-nav${navOpen ? " app-nav--open" : ""}`}
        aria-hidden={!navOpen}
        onMouseEnter={cancelNavHide}
        onMouseLeave={() => navOpen && scheduleNavHide()}
      >
        <div className="app-nav__title">Ultrasound Reporting</div>
        <ul>
          {NAV_ITEMS.map((item) => (
            <li key={item.key}>
              <button
                type="button"
                className={item.key === activePage ? "active" : ""}
                onClick={() => handleNavItemClick(item.key)}
              >
                {item.label}
              </button>
            </li>
          ))}
        </ul>
        <ConnectionStatusIndicators variant="nav" bridge={bridgeStatus} machine={machine} />
      </nav>

      <main className="app-content">
        {renderPage({
          page: activePage,
          patientListTab,
          onPatientListTabChange: setPatientListTab,
          patientDetailStudy,
          reportPreviewStudy,
          onOpenReport: handleOpenReport,
          onBackFromReportPreview: handleBackFromReportPreview,
          reportPrintStudy,
          reportPrintMode,
          onOpenPatient: handleOpenPatient,
          onBackFromPatientDetail: handleBackFromPatientDetail,
          onPrintReport: handlePrintReport,
          onEditAndReprintReport: handleEditAndReprintReport,
          onOpenDicomInspector: handleOpenDicomInspector,
          onBackFromDicomInspector: handleBackFromDicomInspector,
          onOpenReportTemplateBuilder: handleOpenReportTemplateBuilder,
          onBackFromReportTemplateBuilder: handleBackFromReportTemplateBuilder
        })}
      </main>
    </div>
  );
}
