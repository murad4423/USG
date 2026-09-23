/**
 * IPC helpers for the Report Template Builder (Settings > Report Template):
 * loading, saving and deleting the templates the operator builds there.
 * Kept in its own file so ipc.ts stays untouched. Uses the same WebView2
 * bridge (window.chrome.webview) and the same request/"...Result" reply
 * pattern as ipc.ts / ipcImageTemplate.ts. The host side lives in
 * UltrasoundApp.Host/WebViewBridge.ReportTemplates.cs.
 */

export type ReportPageSize = "A4" | "Letter";

/** Page margins in inches. */
export interface ReportTemplateMargins {
  top: number;
  right: number;
  bottom: number;
  left: number;
}

/** A template as stored on disk (one JSON file per template under data/report-builder-templates). */
export interface SavedReportTemplate {
  id: string;
  name: string;
  pageSize: ReportPageSize;
  margins: ReportTemplateMargins;
  /** The Findings text exactly as typed/formatted in the builder (HTML). */
  html: string;
  createdAt?: string;
  updatedAt?: string;
}

/** What the builder sends to save: no id = create a new template, id = update that one. */
export interface ReportTemplateInput {
  id?: string;
  name: string;
  pageSize: ReportPageSize;
  margins: ReportTemplateMargins;
  html: string;
}

/** Reply shape for "getReportBuilderTemplates" / "saveReportBuilderTemplate" / "deleteReportBuilderTemplate". */
export interface ReportTemplateResult {
  success: boolean;
  error?: string;
  /** Set by "getReportBuilderTemplates". */
  templates?: SavedReportTemplate[] | null;
  /** Set by "saveReportBuilderTemplate" — the template as stored (with its id/timestamps). */
  template?: SavedReportTemplate | null;
}

interface ReportTemplateReply extends ReportTemplateResult {
  type: string;
}

function requestReportTemplates(
  requestType: string,
  unavailableMessage: string,
  payload?: Record<string, unknown>
): Promise<ReportTemplateResult> {
  const bridge = window.chrome?.webview;
  if (!bridge) {
    return Promise.resolve({ success: false, error: unavailableMessage });
  }

  return new Promise<ReportTemplateResult>((resolve) => {
    const handleMessage = (event: MessageEvent<string>) => {
      let message: ReportTemplateReply;
      try {
        message = JSON.parse(event.data);
      } catch {
        return;
      }

      if (message.type === `${requestType}Result`) {
        bridge.removeEventListener("message", handleMessage);
        const { type: _type, ...result } = message;
        resolve(result);
      }
    };

    bridge.addEventListener("message", handleMessage);
    bridge.postMessage(JSON.stringify({ type: requestType, ...payload }));
  });
}

/** Every saved report template (sorted by name). */
export function getReportTemplates(): Promise<ReportTemplateResult> {
  return requestReportTemplates(
    "getReportBuilderTemplates",
    "The native bridge isn't available. Run this inside the desktop app."
  );
}

/** Creates (no id) or updates (with id) a template. Resolves with the stored template. */
export function saveReportTemplate(template: ReportTemplateInput): Promise<ReportTemplateResult> {
  return requestReportTemplates(
    "saveReportBuilderTemplate",
    "The native bridge isn't available. Run this inside the desktop app to save the template.",
    { template }
  );
}

/** Permanently deletes a saved template. */
export function deleteReportTemplate(id: string): Promise<ReportTemplateResult> {
  return requestReportTemplates(
    "deleteReportBuilderTemplate",
    "The native bridge isn't available. Run this inside the desktop app to delete the template.",
    { id }
  );
}
