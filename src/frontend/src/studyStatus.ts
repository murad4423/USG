import type { StudyTabKey } from "./components/StudyTabs";

/**
 * Study workflow status (Study.Status in the database). A study moves
 * forward: Received (shown as "Waiting Study") -> ImagePrinted ->
 * ReportPrinted -> Completed. The strings MUST match StudyStatuses.cs on the
 * host. Only "ImagePrinted" is set by the app so far (when a study's images
 * are printed); the later steps are already understood here so their tabs can
 * be switched on later without touching this logic.
 */
export const STUDY_STATUS_IMAGE_PRINTED = "ImagePrinted";

function isStatus(status: string | undefined, ...expected: string[]): boolean {
  const value = (status ?? "").trim().toLowerCase();
  return expected.some((candidate) => candidate.toLowerCase() === value);
}

export interface StudyProgress {
  imagePrinted: boolean;
  reportPrinted: boolean;
  completed: boolean;
}

/** Which of the three circles in the Patient List row are "on" (green) for this status. */
export function studyProgress(status: string | undefined): StudyProgress {
  const completed = isStatus(status, "Completed");
  const reportPrinted = completed || isStatus(status, "ReportPrinted");
  const imagePrinted = reportPrinted || isStatus(status, STUDY_STATUS_IMAGE_PRINTED);
  return { imagePrinted, reportPrinted, completed };
}

/** True if the study should be listed on the given Patient List tab. */
export function studyBelongsToTab(status: string | undefined, tab: StudyTabKey): boolean {
  const progress = studyProgress(status);
  switch (tab) {
    case "waiting":
      return !progress.imagePrinted;
    case "imagePrinted":
      // Images printed, but not yet past that step.
      return progress.imagePrinted && !progress.reportPrinted;
    case "reportPrinted":
      return progress.reportPrinted && !progress.completed;
    case "completed":
      return progress.completed;
  }
}

/** Human-readable status for screens that show it as text (e.g. Patient Detail). */
export function studyStatusLabel(status: string | undefined): string {
  const progress = studyProgress(status);
  if (progress.completed) {
    return "Completed";
  }
  if (progress.reportPrinted) {
    return "Report Printed";
  }
  if (progress.imagePrinted) {
    return "Image Printed";
  }
  return "Waiting";
}
