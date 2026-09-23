export type StudyTabKey = "waiting" | "imagePrinted" | "reportPrinted" | "completed";

interface StudyTabDefinition {
  key: StudyTabKey;
  label: string;
  /**
   * Only tabs with `enabled: true` react to clicks. The other tabs are
   * shown in the UI already but do nothing yet — once their filtering is
   * implemented, just flip the flag to `true` here.
   */
  enabled: boolean;
}

const STUDY_TABS: StudyTabDefinition[] = [
  { key: "waiting", label: "Waiting Study", enabled: true },
  { key: "imagePrinted", label: "Image Printed", enabled: true },
  { key: "reportPrinted", label: "Report Printed", enabled: false },
  { key: "completed", label: "Completed", enabled: false }
];

interface StudyTabsProps {
  active: StudyTabKey;
  onChange: (key: StudyTabKey) => void;
}

/**
 * Tab strip shown between the search/filter toolbar and the patient table.
 * "Waiting Study" (studies whose images are not printed yet) and "Image
 * Printed" are functional; the remaining tabs are visual placeholders for
 * now (see `enabled` in STUDY_TABS above).
 */
export default function StudyTabs({ active, onChange }: StudyTabsProps) {
  return (
    <div className="study-tabs" role="tablist" aria-label="Study filter tabs">
      {STUDY_TABS.map((tab) => {
        const isActive = tab.key === active;
        return (
          <button
            key={tab.key}
            type="button"
            role="tab"
            aria-selected={isActive}
            className={`study-tabs__tab${isActive ? " study-tabs__tab--active" : ""}`}
            onClick={() => {
              if (tab.enabled) {
                onChange(tab.key);
              }
            }}
          >
            {tab.label}
          </button>
        );
      })}
    </div>
  );
}
