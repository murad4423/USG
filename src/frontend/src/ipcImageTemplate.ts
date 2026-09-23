/**
 * IPC helpers for the Settings screen's "Image Template" (PDF) upload.
 * Kept in its own file so ipc.ts stays untouched. Uses the same WebView2
 * bridge (window.chrome.webview) and the same request/"...Result" reply
 * pattern as ipc.ts.
 */

/**
 * The rectangle on the template page inside which the selected images are
 * printed (everything outside it — the hospital's header/footer — stays
 * visible). All four values are FRACTIONS of the page (0..1) measured from
 * the top-left corner, so they are independent of screen size, zoom and
 * print resolution.
 */
export interface ContentArea {
  x: number;
  y: number;
  width: number;
  height: number;
}

/**
 * One patient-information "placeholder" box placed on the template page
 * (e.g. the patient's name printed in the template's header). Like
 * ContentArea, x/y/width/height are FRACTIONS of the page (0..1) measured
 * from the top-left corner. `field` is a key from placeholderFields.ts
 * (PLACEHOLDER_FIELDS). The box only marks WHERE the text goes and how much
 * room it has; the look of the text is set by the optional fields below.
 */
export interface TemplatePlaceholder {
  id: string;
  field: string;
  x: number;
  y: number;
  width: number;
  height: number;
  bold?: boolean;
  align?: "left" | "center" | "right";
  /**
   * Font size in points (1 pt = 1/72 inch on the printed page). Missing on
   * placeholders saved by the first version — then it is derived from the box
   * height (see effectiveFontPoints in placeholderFields.ts).
   */
  fontSize?: number;
  /** Font family name (a Windows font such as "Arial"); empty/missing = the app's default font. */
  fontFamily?: string;
  /** Text colour as "#RRGGBB"; missing = black. */
  color?: string;
}

/** Reply shape for "getImageTemplate" / "uploadImageTemplate" / "saveImageTemplateContentArea" / "saveImageTemplatePlaceholders". */
export interface ImageTemplateResult {
  success: boolean;
  /** Set (instead of an error) when the operator closed the file picker without choosing a file. */
  cancelled?: boolean;
  error?: string;
  /** True when an image template PDF is currently stored. */
  hasTemplate?: boolean;
  /** File name (not full path) of the currently stored template. */
  fileName?: string;
  /**
   * "https://imagetemplate/...?v=..." URL for the cached, rasterized
   * background image of the template's first page, or undefined if there's
   * no template / it couldn't be rendered. Loadable directly as a CSS
   * background-image — no extra conversion needed on the frontend side.
   */
  backgroundImageUrl?: string;
  /** Saved image area for the current template, or null/undefined if none has been selected yet. */
  contentArea?: ContentArea | null;
  /** Saved patient-information placeholders for the current template (empty/null if none). */
  placeholders?: TemplatePlaceholder[] | null;
}

interface ImageTemplateReply extends ImageTemplateResult {
  type: string;
}

function requestImageTemplate(
  requestType: string,
  unavailableMessage: string,
  payload?: Record<string, unknown>
): Promise<ImageTemplateResult> {
  const bridge = window.chrome?.webview;
  if (!bridge) {
    return Promise.resolve({ success: false, error: unavailableMessage });
  }

  return new Promise<ImageTemplateResult>((resolve) => {
    const handleMessage = (event: MessageEvent<string>) => {
      let message: ImageTemplateReply;
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

/** Which image template PDF (if any) is currently stored. */
export function getImageTemplate(): Promise<ImageTemplateResult> {
  return requestImageTemplate(
    "getImageTemplate",
    "The native bridge isn't available. Run this inside the desktop app."
  );
}

/**
 * Opens a native file picker for a .pdf, validates it and stores it as the
 * app's image template (replacing any previous one). A cancelled pick comes
 * back as { success: false, cancelled: true }, not a rejection.
 */
export function uploadImageTemplate(): Promise<ImageTemplateResult> {
  return requestImageTemplate(
    "uploadImageTemplate",
    "The native bridge isn't available. Run this inside the desktop app to upload a template."
  );
}

/**
 * Saves the image area (see ContentArea) for the current template, or
 * clears it when `area` is null (sheets then use their normal margins
 * again). Takes effect immediately for the live preview and for every
 * print composed afterwards; can be changed again at any time.
 */
export function saveImageTemplateContentArea(area: ContentArea | null): Promise<ImageTemplateResult> {
  return requestImageTemplate(
    "saveImageTemplateContentArea",
    "The native bridge isn't available. Run this inside the desktop app to save the image area.",
    { contentArea: area }
  );
}

/**
 * Saves the patient-information placeholders (see TemplatePlaceholder) for
 * the current template, replacing the previously saved list. Pass an empty
 * array to remove them all. Takes effect immediately for the live preview
 * and for every print composed afterwards.
 */
export function saveImageTemplatePlaceholders(placeholders: TemplatePlaceholder[]): Promise<ImageTemplateResult> {
  return requestImageTemplate(
    "saveImageTemplatePlaceholders",
    "The native bridge isn't available. Run this inside the desktop app to save the placeholders.",
    { placeholders }
  );
}
