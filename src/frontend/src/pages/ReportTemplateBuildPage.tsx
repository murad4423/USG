import { useEffect, useRef, useState } from "react";
import {
  deleteReportTemplate,
  getReportTemplates,
  saveReportTemplate,
  type ReportPageSize,
  type ReportTemplateMargins,
  type SavedReportTemplate
} from "../ipcReportTemplates";
import "../styles/report-template-builder-extras.css";

interface ReportTemplateBuildPageProps {
  /** Returns to the Settings page this builder was opened from. */
  onBack: () => void;
}

type PageSizeKey = ReportPageSize;
type Alignment = "left" | "center" | "right" | "justify";
type MarginSide = keyof ReportTemplateMargins;

// Page dimensions in CSS px at 96 DPI (screen-standard), at 100% zoom —
// the same numbers used elsewhere in this app for print-preview sizing.
const PAGE_SIZES: Record<PageSizeKey, { width: number; height: number; label: string }> = {
  A4: { width: 794, height: 1123, label: "A4" },
  Letter: { width: 816, height: 1056, label: "Letter" }
};

const FONT_FAMILIES = [
  "Nirmala UI",
  "Segoe UI",
  "Arial",
  "Calibri",
  "Times New Roman",
  "Courier New"
];

const FONT_SIZES_PT = [8, 9, 10, 10.5, 11, 12, 14, 16, 18, 20, 24, 28, 32];

// What a brand-new, empty canvas is drawn with (see .report-template-build__canvas in
// global.css) — shown in the Font / Size boxes until the operator picks something else.
const DEFAULT_FONT = "Nirmala UI";
const DEFAULT_FONT_SIZE_PT = 12;

// Margin presets, in inches — each one sets all four sides. Applied as padding
// on the page (1in = 96px at the 96 DPI this canvas is drawn at).
const MARGIN_PRESETS: { value: number; label: string }[] = [
  { value: 0.5, label: 'Narrow (0.5")' },
  { value: 0.75, label: 'Moderate (0.75")' },
  { value: 1, label: 'Normal (1")' },
  { value: 1.5, label: 'Wide (1.5")' }
];

const MARGIN_SIDES: { key: MarginSide; label: string }[] = [
  { key: "top", label: "Top" },
  { key: "bottom", label: "Bottom" },
  { key: "left", label: "Left" },
  { key: "right", label: "Right" }
];

const MAX_MARGIN_IN = 3;
const DEFAULT_MARGINS: ReportTemplateMargins = { top: 1, right: 1, bottom: 1, left: 1 };

const MIN_ZOOM = 20;
const MAX_ZOOM = 200;
const ZOOM_STEP = 10;
// Padding inside the scrollable canvas area (see .report-template-build__canvas-scroll),
// subtracted when computing how much room the page has to fit into.
const CANVAS_SCROLL_PADDING = 48;

/**
 * The canvas HTML is stored on disk and put back with innerHTML when a
 * template is opened for editing — so scripts, event-handler attributes and
 * javascript: links are stripped first, whatever ended up in the file.
 */
function sanitizeHtml(html: string): string {
  const doc = new DOMParser().parseFromString(html, "text/html");
  doc.querySelectorAll("script, style, iframe, object, embed, link, meta, base, form").forEach((el) => el.remove());
  doc.body.querySelectorAll("*").forEach((el) => {
    for (const attr of Array.from(el.attributes)) {
      const name = attr.name.toLowerCase();
      const isScriptUrl = (name === "href" || name === "src") && /^\s*javascript:/i.test(attr.value);
      if (name.startsWith("on") || isScriptUrl) {
        el.removeAttribute(attr.name);
      }
    }
  });
  return doc.body.innerHTML;
}

function formatTimestamp(value?: string): string {
  if (!value) {
    return "";
  }
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? "" : date.toLocaleString();
}

function AlignIcon({ kind }: { kind: Alignment }) {
  // Four text lines inside a 16x16 box: full-width lines are 12 wide, the
  // short ones 7 wide (justified text has no short lines).
  const short = kind === "justify" ? 12 : 7;
  const widths = [12, short, 12, short];
  const startX = (width: number) => {
    if (kind === "right") {
      return 14 - width;
    }
    if (kind === "center") {
      return (16 - width) / 2;
    }
    return 2;
  };

  return (
    <svg width="16" height="16" viewBox="0 0 16 16" aria-hidden="true">
      {widths.map((width, index) => (
        <line
          key={index}
          x1={startX(width)}
          x2={startX(width) + width}
          y1={2.5 + index * 3.7}
          y2={2.5 + index * 3.7}
          stroke="currentColor"
          strokeWidth="1.6"
          strokeLinecap="round"
        />
      ))}
    </svg>
  );
}

function BulletListIcon() {
  return (
    <svg width="16" height="16" viewBox="0 0 16 16" aria-hidden="true">
      {[3, 8, 13].map((y) => (
        <g key={y}>
          <circle cx="2.8" cy={y} r="1.3" fill="currentColor" />
          <line x1="6.5" x2="14" y1={y} y2={y} stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" />
        </g>
      ))}
    </svg>
  );
}

/**
 * Settings tool: "Report Template" builder.
 *
 * A report has 3 parts — Patient Information, Findings, and Doctor
 * Information. This builder currently only covers the Findings part.
 *
 * "list" view: the "Create Template" button, and under it every template
 * saved so far, each with Edit and Delete.
 *
 * Flow: an operator clicks "Create Template", enters an Investigation
 * Name / Template Name (e.g. "Whole Abdomen"), and lands on a three-pane
 * editor: the canvas takes the left half of the workspace (click and type
 * like a blank page in MS Office, Enter starts a new line), a narrow
 * vertical toolbar sits next to it (page size, margin preset, font, size,
 * bold/italic/underline, alignment, bullets, color, zoom), and the remaining
 * space on the right is the options panel (the four page margins). A Save
 * button in the bar above the workspace stores the template — page size,
 * margins and the formatted text — through the host bridge
 * (see ipcReportTemplates.ts).
 */
export default function ReportTemplateBuildPage({ onBack }: ReportTemplateBuildPageProps) {
  // "list": the "Create Template" button + saved templates.
  // "naming": the Investigation Name / Template Name prompt.
  // "editor": the canvas + toolbar + options-panel layout for the template being edited.
  const [mode, setMode] = useState<"list" | "naming" | "editor">("list");
  const [templateNameDraft, setTemplateNameDraft] = useState("");
  const [namingError, setNamingError] = useState<string | undefined>();

  // Saved templates (the list view).
  const [templates, setTemplates] = useState<SavedReportTemplate[]>([]);
  const [listState, setListState] = useState<"loading" | "loaded" | "error">("loading");
  const [listError, setListError] = useState<string | undefined>();
  const [deleteTarget, setDeleteTarget] = useState<SavedReportTemplate | null>(null);
  const [deleting, setDeleting] = useState(false);

  // The template open in the editor. editingId is null until its first save.
  const [editingId, setEditingId] = useState<string | null>(null);
  const [templateName, setTemplateName] = useState("");
  const [pageSize, setPageSize] = useState<PageSizeKey>("A4");
  const [margins, setMargins] = useState<ReportTemplateMargins>(DEFAULT_MARGINS);
  const [showMarginGuides, setShowMarginGuides] = useState(true);
  const [marginDialogOpen, setMarginDialogOpen] = useState(false);
  const [zoom, setZoom] = useState(100);
  const [dirty, setDirty] = useState(false);
  const [saveState, setSaveState] = useState<"idle" | "saving" | "saved" | "error">("idle");
  const [saveMessage, setSaveMessage] = useState<string | undefined>();
  // Set when the operator tries to leave the editor with unsaved changes.
  const [leaveTarget, setLeaveTarget] = useState<"list" | "settings" | null>(null);

  // Formatting state at the caret / selection — mirrored into the toolbar.
  const [activeFormats, setActiveFormats] = useState({ bold: false, italic: false, underline: false, bullets: false });
  const [alignment, setAlignment] = useState<Alignment>("left");
  const [fontName, setFontName] = useState(DEFAULT_FONT);
  const [fontSizePt, setFontSizePt] = useState(DEFAULT_FONT_SIZE_PT);

  const activePage = PAGE_SIZES[pageSize];
  const marginPx = {
    top: margins.top * 96,
    right: margins.right * 96,
    bottom: margins.bottom * 96,
    left: margins.left * 96
  };
  const activePreset = MARGIN_PRESETS.find((preset) =>
    MARGIN_SIDES.every(({ key }) => Math.abs(margins[key] - preset.value) < 0.001)
  );
  const fontOptions = FONT_FAMILIES.includes(fontName) ? FONT_FAMILIES : [fontName, ...FONT_FAMILIES];
  const sizeOptions = FONT_SIZES_PT.includes(fontSizePt)
    ? FONT_SIZES_PT
    : [...FONT_SIZES_PT, fontSizePt].sort((a, b) => a - b);

  // The canvas is an uncontrolled contentEditable element (its HTML is not
  // mirrored into React state) — that's deliberate, so typing/formatting
  // never gets fought by a re-render moving the caret. Its HTML is set once
  // when the editor opens (see the mode effect below) and read back on save.
  const canvasRef = useRef<HTMLDivElement | null>(null);
  // The HTML to load into the canvas the next time the editor opens.
  const loadedHtmlRef = useRef("");
  // The scrollable area the page sits in — measured to compute the zoom
  // level that fits the whole page on screen (see fitToPage()).
  const canvasScrollRef = useRef<HTMLDivElement | null>(null);
  // The canvas-scroll size fitToPage() last computed against. Used to tell
  // a real window resize apart from the container's size flickering by a
  // few px when a scrollbar appears/disappears as the operator zooms in —
  // without this, that flicker would re-trigger fitToPage() and snap the
  // zoom straight back down every time it was pushed past the fitted size.
  const lastFitSizeRef = useRef({ width: 0, height: 0 });
  // Selects/color-pickers steal focus (and with it, the text selection) the
  // moment they're opened, before their onChange fires. We remember the
  // last real selection made inside the canvas so it can be restored right
  // before applying a formatting command from one of those controls.
  const savedRangeRef = useRef<Range | null>(null);

  function fitToPage() {
    const container = canvasScrollRef.current;
    if (!container) {
      return;
    }
    const availableWidth = container.clientWidth - CANVAS_SCROLL_PADDING;
    const availableHeight = container.clientHeight - CANVAS_SCROLL_PADDING;
    if (availableWidth <= 0 || availableHeight <= 0) {
      return;
    }
    const widthRatio = (availableWidth / activePage.width) * 100;
    const heightRatio = (availableHeight / activePage.height) * 100;
    const fit = Math.floor(Math.min(widthRatio, heightRatio));
    setZoom(Math.max(MIN_ZOOM, Math.min(MAX_ZOOM, fit)));
    lastFitSizeRef.current = { width: container.clientWidth, height: container.clientHeight };
  }

  // Load the saved templates when the builder opens.
  useEffect(() => {
    let cancelled = false;

    async function refreshTemplates() {
      const result = await getReportTemplates();
      if (cancelled) {
        return;
      }
      if (result.success) {
        setTemplates(result.templates ?? []);
        setListState("loaded");
      } else {
        setListError(result.error ?? "Could not load the saved templates.");
        setListState("error");
      }
    }

    void refreshTemplates();

    return () => {
      cancelled = true;
    };
  }, []);

  // Fill the canvas with the template's saved text when the editor opens.
  useEffect(() => {
    if (mode !== "editor") {
      return;
    }
    const canvas = canvasRef.current;
    if (canvas) {
      canvas.innerHTML = sanitizeHtml(loadedHtmlRef.current);
    }
    savedRangeRef.current = null;
    setActiveFormats({ bold: false, italic: false, underline: false, bullets: false });
    setAlignment("left");
    setFontName(DEFAULT_FONT);
    setFontSizePt(DEFAULT_FONT_SIZE_PT);
  }, [mode]);

  // Fit the page into view when the editor opens or the page size changes,
  // and again on a genuine window resize — but not on every pixel-level
  // change (a zoomed-in page toggling the scrollbar on/off nudges the
  // container's clientWidth by ~15-17px; anything under that is ignored so
  // it doesn't fight the operator's own zoom in/out).
  useEffect(() => {
    if (mode !== "editor") {
      return;
    }
    fitToPage();

    function handleWindowResize() {
      const container = canvasScrollRef.current;
      if (!container) {
        return;
      }
      const widthDelta = Math.abs(container.clientWidth - lastFitSizeRef.current.width);
      const heightDelta = Math.abs(container.clientHeight - lastFitSizeRef.current.height);
      if (widthDelta > 24 || heightDelta > 24) {
        fitToPage();
      }
    }

    window.addEventListener("resize", handleWindowResize);
    return () => window.removeEventListener("resize", handleWindowResize);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [mode, pageSize]);

  /**
   * Reads the formatting at the caret / selection start (bold, alignment,
   * bullets, and the real font name + size) into the toolbar, and remembers
   * the selection so a select / color picker can restore it later.
   */
  function readToolbarState() {
    const canvas = canvasRef.current;
    const selection = window.getSelection();
    if (!canvas || !selection || selection.rangeCount === 0) {
      return;
    }
    const range = selection.getRangeAt(0);
    if (!canvas.contains(range.commonAncestorContainer)) {
      return;
    }
    savedRangeRef.current = range.cloneRange();

    setActiveFormats({
      bold: document.queryCommandState("bold"),
      italic: document.queryCommandState("italic"),
      underline: document.queryCommandState("underline"),
      bullets: document.queryCommandState("insertUnorderedList")
    });

    if (document.queryCommandState("justifyCenter")) {
      setAlignment("center");
    } else if (document.queryCommandState("justifyRight")) {
      setAlignment("right");
    } else if (document.queryCommandState("justifyFull")) {
      setAlignment("justify");
    } else {
      setAlignment("left");
    }

    // The font actually in effect where the caret is (the computed style, so
    // inherited / default fonts are reported too, not just ones we applied).
    const node = range.startContainer;
    const element = node.nodeType === Node.ELEMENT_NODE ? (node as Element) : node.parentElement;
    if (element) {
      const style = window.getComputedStyle(element);
      const firstFamily = style.fontFamily.split(",")[0].trim().replace(/^["']|["']$/g, "");
      if (firstFamily) {
        const known = FONT_FAMILIES.find((font) => font.toLowerCase() === firstFamily.toLowerCase());
        setFontName(known ?? firstFamily);
      }
      const sizePx = parseFloat(style.fontSize);
      if (!Number.isNaN(sizePx)) {
        // px -> pt (96 DPI), rounded to the nearest half point.
        setFontSizePt(Math.round(sizePx * 0.75 * 2) / 2);
      }
    }
  }

  useEffect(() => {
    document.addEventListener("selectionchange", readToolbarState);
    return () => document.removeEventListener("selectionchange", readToolbarState);
    // readToolbarState only touches refs and state setters, so the first render's copy is fine.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  useEffect(() => {
    if (!marginDialogOpen) {
      return;
    }
    function handleKeyDown(event: KeyboardEvent) {
      if (event.key === "Escape") {
        setMarginDialogOpen(false);
      }
    }
    document.addEventListener("keydown", handleKeyDown);
    return () => document.removeEventListener("keydown", handleKeyDown);
  }, [marginDialogOpen]);

  function markDirty() {
    setDirty(true);
    if (saveState !== "saving") {
      setSaveState("idle");
      setSaveMessage(undefined);
    }
  }

  function focusCanvasAndRestoreSelection() {
    const canvas = canvasRef.current;
    if (!canvas) {
      return;
    }
    canvas.focus();
    const range = savedRangeRef.current;
    const selection = window.getSelection();
    if (range && selection) {
      selection.removeAllRanges();
      selection.addRange(range);
    }
  }

  function exec(command: string, value?: string) {
    focusCanvasAndRestoreSelection();
    document.execCommand(command, false, value);
    canvasRef.current?.focus();
    readToolbarState();
    markDirty();
  }

  function applyFontName(name: string) {
    exec("fontName", name);
    setFontName(name);
  }

  // execCommand("fontSize") only understands the legacy 1-7 scale, so for a
  // selection we apply size "7" and then swap the resulting <font size="7">
  // tags for a span with the real pt size the operator picked. With just a
  // caret (nothing selected) there's nothing to wrap yet, so an empty span
  // is inserted at the caret and the caret is put inside it — whatever is
  // typed next gets the new size.
  function applyFontSize(sizePt: number) {
    focusCanvasAndRestoreSelection();
    const canvas = canvasRef.current;
    const selection = window.getSelection();
    if (!canvas || !selection || selection.rangeCount === 0) {
      return;
    }

    const range = selection.getRangeAt(0);
    if (range.collapsed) {
      const span = document.createElement("span");
      span.style.fontSize = `${sizePt}pt`;
      const textNode = document.createTextNode("\u200B");
      span.appendChild(textNode);
      range.insertNode(span);

      const caret = document.createRange();
      caret.setStart(textNode, 1);
      caret.collapse(true);
      selection.removeAllRanges();
      selection.addRange(caret);
    } else {
      document.execCommand("fontSize", false, "7");
      canvas.querySelectorAll('font[size="7"]').forEach((el) => {
        const span = document.createElement("span");
        span.style.fontSize = `${sizePt}pt`;
        span.innerHTML = (el as HTMLElement).innerHTML;
        el.replaceWith(span);
      });
    }

    canvas.focus();
    setFontSizePt(sizePt);
    markDirty();
  }

  function zoomIn() {
    setZoom((current) => Math.min(MAX_ZOOM, current + ZOOM_STEP));
  }

  function zoomOut() {
    setZoom((current) => Math.max(MIN_ZOOM, current - ZOOM_STEP));
  }

  function updateMargin(side: MarginSide, rawValue: string) {
    const parsed = Number(rawValue);
    const value = Number.isNaN(parsed) ? 0 : Math.min(MAX_MARGIN_IN, Math.max(0, parsed));
    setMargins((current) => ({ ...current, [side]: value }));
    markDirty();
  }

  function applyMarginPreset(value: number) {
    setMargins({ top: value, right: value, bottom: value, left: value });
    markDirty();
  }

  function changePageSize(next: PageSizeKey) {
    setPageSize(next);
    markDirty();
  }

  // ----- list view: create / edit / delete -----

  function openNamingPrompt() {
    setTemplateNameDraft("");
    setNamingError(undefined);
    setMode("naming");
  }

  function cancelNamingPrompt() {
    setMode("list");
  }

  function openEditor(template: SavedReportTemplate | null, name: string) {
    setEditingId(template?.id ?? null);
    setTemplateName(name);
    setPageSize(template?.pageSize ?? "A4");
    setMargins(template?.margins ?? DEFAULT_MARGINS);
    loadedHtmlRef.current = template?.html ?? "";
    setDirty(false);
    setSaveState("idle");
    setSaveMessage(undefined);
    setMode("editor");
  }

  function handleCreateTemplate() {
    const trimmed = templateNameDraft.trim();
    if (!trimmed) {
      return;
    }
    if (templates.some((template) => template.name.toLowerCase() === trimmed.toLowerCase())) {
      setNamingError(`A template named "${trimmed}" already exists. Choose a different name.`);
      return;
    }
    openEditor(null, trimmed);
  }

  async function handleConfirmDelete() {
    if (!deleteTarget) {
      return;
    }
    setDeleting(true);
    const result = await deleteReportTemplate(deleteTarget.id);
    setDeleting(false);

    if (result.success) {
      setTemplates((current) => current.filter((template) => template.id !== deleteTarget.id));
      setListError(undefined);
    } else {
      setListError(result.error ?? "Could not delete the template.");
    }
    setDeleteTarget(null);
  }

  // ----- editor: save / leave -----

  async function handleSave() {
    const canvas = canvasRef.current;
    const name = templateName.trim();
    if (!canvas) {
      return;
    }
    if (!name) {
      setSaveState("error");
      setSaveMessage("Enter a template name before saving.");
      return;
    }

    setSaveState("saving");
    setSaveMessage(undefined);

    const result = await saveReportTemplate({
      id: editingId ?? undefined,
      name,
      pageSize,
      margins,
      // The zero-width spaces are only the placeholders used to carry a font size at an empty caret.
      html: canvas.innerHTML.replace(/\u200B/g, "")
    });

    if (!result.success || !result.template) {
      setSaveState("error");
      setSaveMessage(result.error ?? "Could not save the template.");
      return;
    }

    const stored = result.template;
    setEditingId(stored.id);
    setTemplateName(stored.name);
    setTemplates((current) =>
      [...current.filter((template) => template.id !== stored.id), stored].sort((a, b) =>
        a.name.localeCompare(b.name, undefined, { sensitivity: "base" })
      )
    );
    setDirty(false);
    setSaveState("saved");
    setSaveMessage("Saved.");
  }

  function backToList() {
    setLeaveTarget(null);
    setMarginDialogOpen(false);
    setEditingId(null);
    setMode("list");
  }

  function requestLeaveEditor(target: "list" | "settings") {
    if (!dirty) {
      if (target === "list") {
        backToList();
      } else {
        onBack();
      }
      return;
    }
    setLeaveTarget(target);
  }

  function confirmLeaveEditor() {
    const target = leaveTarget;
    if (target === "settings") {
      setLeaveTarget(null);
      onBack();
    } else {
      backToList();
    }
  }

  function handleBackToSettings() {
    if (mode === "editor") {
      requestLeaveEditor("settings");
    } else {
      onBack();
    }
  }

  return (
    <div className={`page${mode === "editor" ? " report-template-build__page" : ""}`}>
      <div className="report-template-build__header">
        <button type="button" className="report-template-build__back-button" onClick={handleBackToSettings}>
          ← Back to Settings
        </button>
        <h1>Report Template Builder</h1>
      </div>

      {mode === "editor" ? (
        <>
          <div className="report-template-build__editor-bar">
            <input
              type="text"
              className="report-template-build__title-input"
              value={templateName}
              maxLength={120}
              placeholder="Template name"
              onChange={(event) => {
                setTemplateName(event.target.value);
                markDirty();
              }}
            />
            <div className="report-template-build__editor-bar-actions">
              {saveMessage && (
                <span
                  className={`report-template-build__save-message${
                    saveState === "error" ? " report-template-build__save-message--error" : ""
                  }`}
                >
                  {saveState === "error" ? "⚠️" : "✓"} {saveMessage}
                </span>
              )}
              {dirty && saveState !== "error" && !saveMessage && (
                <span className="report-template-build__save-message report-template-build__save-message--pending">
                  Unsaved changes
                </span>
              )}
              <button
                type="button"
                className="report-template-build__back-button"
                onClick={() => requestLeaveEditor("list")}
              >
                ← All Templates
              </button>
              <button
                type="button"
                className="settings__tool-button"
                onClick={handleSave}
                disabled={saveState === "saving"}
              >
                {saveState === "saving" ? "Saving..." : "Save"}
              </button>
            </div>
          </div>

          <div className="report-template-build__workspace">
            <div className="report-template-build__canvas-area">
              <div className="report-template-build__canvas-scroll" ref={canvasScrollRef}>
                <div
                  className="report-template-build__canvas-page"
                  style={{
                    width: (activePage.width * zoom) / 100,
                    height: (activePage.height * zoom) / 100
                  }}
                >
                  <div
                    ref={canvasRef}
                    className="report-template-build__canvas"
                    contentEditable
                    suppressContentEditableWarning
                    data-placeholder="Click here to start typing the Findings text..."
                    onInput={markDirty}
                    style={{
                      width: activePage.width,
                      height: activePage.height,
                      padding: `${marginPx.top}px ${marginPx.right}px ${marginPx.bottom}px ${marginPx.left}px`,
                      transform: `scale(${zoom / 100})`
                    }}
                  />
                  {showMarginGuides && (
                    <div
                      className="report-template-build__margin-guides"
                      style={{
                        top: (marginPx.top * zoom) / 100,
                        right: (marginPx.right * zoom) / 100,
                        bottom: (marginPx.bottom * zoom) / 100,
                        left: (marginPx.left * zoom) / 100
                      }}
                    />
                  )}
                </div>
              </div>
            </div>

            <div className="report-template-build__toolbar-sidebar">
              <label className="report-template-build__toolbar-field">
                <span className="report-template-build__toolbar-field-label">Page Size</span>
                <select
                  className="report-template-build__toolbar-select-full"
                  value={pageSize}
                  onChange={(event) => changePageSize(event.target.value as PageSizeKey)}
                >
                  {(Object.keys(PAGE_SIZES) as PageSizeKey[]).map((key) => (
                    <option key={key} value={key}>
                      {PAGE_SIZES[key].label}
                    </option>
                  ))}
                </select>
              </label>

              <div className="report-template-build__toolbar-field">
                <span className="report-template-build__toolbar-field-label">Margin</span>
                <button
                  type="button"
                  className="report-template-build__toolbar-button--text report-template-build__margin-button"
                  onClick={() => setMarginDialogOpen(true)}
                  title="Set the page margins"
                >
                  {activePreset ? activePreset.label : "Custom"}
                </button>
              </div>

              <div className="report-template-build__toolbar-divider-h" />

              <label className="report-template-build__toolbar-field">
                <span className="report-template-build__toolbar-field-label">Font</span>
                <select
                  className="report-template-build__toolbar-select-full"
                  value={fontName}
                  onChange={(event) => applyFontName(event.target.value)}
                >
                  {fontOptions.map((font) => (
                    <option key={font} value={font} style={{ fontFamily: font }}>
                      {font}
                    </option>
                  ))}
                </select>
              </label>

              <label className="report-template-build__toolbar-field">
                <span className="report-template-build__toolbar-field-label">Size</span>
                <select
                  className="report-template-build__toolbar-select-full"
                  value={fontSizePt}
                  onChange={(event) => applyFontSize(Number(event.target.value))}
                >
                  {sizeOptions.map((size) => (
                    <option key={size} value={size}>
                      {size} pt
                    </option>
                  ))}
                </select>
              </label>

              <div className="report-template-build__toolbar-row">
                <button
                  type="button"
                  className={`report-template-build__toolbar-button${
                    activeFormats.bold ? " report-template-build__toolbar-button--active" : ""
                  }`}
                  onMouseDown={(event) => event.preventDefault()}
                  onClick={() => exec("bold")}
                  title="Bold"
                >
                  <strong>B</strong>
                </button>
                <button
                  type="button"
                  className={`report-template-build__toolbar-button${
                    activeFormats.italic ? " report-template-build__toolbar-button--active" : ""
                  }`}
                  onMouseDown={(event) => event.preventDefault()}
                  onClick={() => exec("italic")}
                  title="Italic"
                >
                  <em>I</em>
                </button>
                <button
                  type="button"
                  className={`report-template-build__toolbar-button${
                    activeFormats.underline ? " report-template-build__toolbar-button--active" : ""
                  }`}
                  onMouseDown={(event) => event.preventDefault()}
                  onClick={() => exec("underline")}
                  title="Underline"
                >
                  <u>U</u>
                </button>
              </div>

              <div className="report-template-build__toolbar-field">
                <span className="report-template-build__toolbar-field-label">Align</span>
                <div className="report-template-build__toolbar-row report-template-build__toolbar-row--tight">
                  {(
                    [
                      { kind: "left", command: "justifyLeft", title: "Align left" },
                      { kind: "center", command: "justifyCenter", title: "Align center" },
                      { kind: "right", command: "justifyRight", title: "Align right" },
                      { kind: "justify", command: "justifyFull", title: "Justify" }
                    ] as { kind: Alignment; command: string; title: string }[]
                  ).map((option) => (
                    <button
                      key={option.kind}
                      type="button"
                      className={`report-template-build__toolbar-button${
                        alignment === option.kind ? " report-template-build__toolbar-button--active" : ""
                      }`}
                      onMouseDown={(event) => event.preventDefault()}
                      onClick={() => exec(option.command)}
                      title={option.title}
                    >
                      <AlignIcon kind={option.kind} />
                    </button>
                  ))}
                </div>
              </div>

              <div className="report-template-build__toolbar-field">
                <span className="report-template-build__toolbar-field-label">List</span>
                <div className="report-template-build__toolbar-row">
                  <button
                    type="button"
                    className={`report-template-build__toolbar-button${
                      activeFormats.bullets ? " report-template-build__toolbar-button--active" : ""
                    }`}
                    onMouseDown={(event) => event.preventDefault()}
                    onClick={() => exec("insertUnorderedList")}
                    title="Bullet list"
                  >
                    <BulletListIcon />
                  </button>
                </div>
              </div>

              <label className="report-template-build__toolbar-field">
                <span className="report-template-build__toolbar-field-label">Color</span>
                <input
                  type="color"
                  className="report-template-build__toolbar-color-full"
                  defaultValue="#1a1a1a"
                  onChange={(event) => exec("foreColor", event.target.value)}
                />
              </label>

              <div className="report-template-build__toolbar-divider-h" />

              <span className="report-template-build__toolbar-field-label">Zoom</span>
              <div className="report-template-build__zoom-group">
                <button
                  type="button"
                  className="report-template-build__toolbar-button"
                  onClick={zoomOut}
                  disabled={zoom <= MIN_ZOOM}
                  title="Zoom out"
                >
                  −
                </button>
                <span className="report-template-build__zoom-value">{zoom}%</span>
                <button
                  type="button"
                  className="report-template-build__toolbar-button"
                  onClick={zoomIn}
                  disabled={zoom >= MAX_ZOOM}
                  title="Zoom in"
                >
                  +
                </button>
              </div>
              <button
                type="button"
                className="report-template-build__toolbar-button--text report-template-build__toolbar-reset"
                onClick={fitToPage}
                title="Fit the whole page in view"
              >
                Fit to Page
              </button>
            </div>

            <aside className="report-template-build__options-panel">
              <h3 className="report-template-build__options-title">Options</h3>

              <p className="report-template-build__options-placeholder">More options coming soon.</p>
            </aside>
          </div>
        </>
      ) : (
        <>
          <div className="report-template-build__create-row">
            <button type="button" className="settings__tool-button" onClick={openNamingPrompt}>
              Create Template
            </button>
          </div>

          <section className="report-template-build__saved">
            <h2 className="report-template-build__saved-title">Saved Templates</h2>

            {listState === "loading" && <p className="report-template-build__saved-note">Loading...</p>}
            {listError && <p className="report-template-build__saved-note report-template-build__saved-note--error">⚠️ {listError}</p>}
            {listState === "loaded" && templates.length === 0 && (
              <p className="report-template-build__saved-note">
                No templates saved yet. Click "Create Template" to make the first one.
              </p>
            )}

            {templates.length > 0 && (
              <ul className="report-template-build__saved-list">
                {templates.map((template) => (
                  <li key={template.id} className="report-template-build__saved-row">
                    <div className="report-template-build__saved-info">
                      <span className="report-template-build__saved-name">{template.name}</span>
                      <span className="report-template-build__saved-meta">
                        {PAGE_SIZES[template.pageSize]?.label ?? template.pageSize}
                        {template.updatedAt ? ` · Updated ${formatTimestamp(template.updatedAt)}` : ""}
                      </span>
                    </div>
                    <div className="report-template-build__saved-actions">
                      <button
                        type="button"
                        className="report-template-build__back-button"
                        onClick={() => openEditor(template, template.name)}
                      >
                        Edit
                      </button>
                      <button
                        type="button"
                        className="report-template-build__delete-button"
                        onClick={() => setDeleteTarget(template)}
                      >
                        Delete
                      </button>
                    </div>
                  </li>
                ))}
              </ul>
            )}
          </section>
        </>
      )}

      {mode === "editor" && marginDialogOpen && (
        <div
          className="report-template-build__modal-backdrop report-template-build__modal-backdrop--side"
          onClick={() => setMarginDialogOpen(false)}
        >
          <div className="report-template-build__modal" onClick={(event) => event.stopPropagation()}>
            <h3 className="report-template-build__modal-title">Page Margins</h3>

            <label className="report-template-build__modal-label">
              Preset
              <select
                className="settings__input"
                value={activePreset ? String(activePreset.value) : "custom"}
                onChange={(event) => {
                  if (event.target.value !== "custom") {
                    applyMarginPreset(Number(event.target.value));
                  }
                }}
              >
                {MARGIN_PRESETS.map((preset) => (
                  <option key={preset.value} value={preset.value}>
                    {preset.label}
                  </option>
                ))}
                {!activePreset && <option value="custom">Custom</option>}
              </select>
            </label>

            <h4 className="report-template-build__options-subtitle report-template-build__margin-dialog-subtitle">
              Custom (inches)
            </h4>
            <div className="report-template-build__margin-grid">
              {MARGIN_SIDES.map(({ key, label }) => (
                <label key={key} className="report-template-build__margin-field">
                  <span className="report-template-build__toolbar-field-label">{label}</span>
                  <input
                    type="number"
                    className="settings__input"
                    min={0}
                    max={MAX_MARGIN_IN}
                    step={0.05}
                    value={margins[key]}
                    onChange={(event) => updateMargin(key, event.target.value)}
                  />
                </label>
              ))}
            </div>
            <p className="report-template-build__options-hint">
              Distance from each page edge to the text. Changes show on the page straight away.
            </p>
            <label className="report-template-build__options-check">
              <input
                type="checkbox"
                checked={showMarginGuides}
                onChange={(event) => setShowMarginGuides(event.target.checked)}
              />
              Show margin guides on the page
            </label>

            <div className="report-template-build__modal-actions">
              <button type="button" className="settings__tool-button" onClick={() => setMarginDialogOpen(false)}>
                Done
              </button>
            </div>
          </div>
        </div>
      )}

      {mode === "naming" && (
        <div className="report-template-build__modal-backdrop" onClick={cancelNamingPrompt}>
          <div className="report-template-build__modal" onClick={(event) => event.stopPropagation()}>
            <h3 className="report-template-build__modal-title">New Report Template</h3>
            <label className="report-template-build__modal-label">
              Investigation Name / Template Name
              <input
                type="text"
                className="settings__input"
                placeholder="e.g. Whole Abdomen"
                autoFocus
                value={templateNameDraft}
                onChange={(event) => {
                  setTemplateNameDraft(event.target.value);
                  setNamingError(undefined);
                }}
                onKeyDown={(event) => {
                  if (event.key === "Enter") {
                    handleCreateTemplate();
                  }
                }}
              />
            </label>
            {namingError && <p className="report-template-build__modal-error">⚠️ {namingError}</p>}
            <div className="report-template-build__modal-actions">
              <button type="button" className="report-template-build__back-button" onClick={cancelNamingPrompt}>
                Cancel
              </button>
              <button
                type="button"
                className="settings__tool-button"
                disabled={!templateNameDraft.trim()}
                onClick={handleCreateTemplate}
              >
                Create
              </button>
            </div>
          </div>
        </div>
      )}

      {deleteTarget && (
        <div className="report-template-build__modal-backdrop" onClick={() => !deleting && setDeleteTarget(null)}>
          <div className="report-template-build__modal" onClick={(event) => event.stopPropagation()}>
            <h3 className="report-template-build__modal-title">Delete template?</h3>
            <p className="report-template-build__modal-text">
              "{deleteTarget.name}" will be deleted permanently. This cannot be undone.
            </p>
            <div className="report-template-build__modal-actions">
              <button
                type="button"
                className="report-template-build__back-button"
                disabled={deleting}
                onClick={() => setDeleteTarget(null)}
              >
                Cancel
              </button>
              <button
                type="button"
                className="report-template-build__delete-button report-template-build__delete-button--solid"
                disabled={deleting}
                onClick={handleConfirmDelete}
              >
                {deleting ? "Deleting..." : "Delete"}
              </button>
            </div>
          </div>
        </div>
      )}

      {leaveTarget && (
        <div className="report-template-build__modal-backdrop" onClick={() => setLeaveTarget(null)}>
          <div className="report-template-build__modal" onClick={(event) => event.stopPropagation()}>
            <h3 className="report-template-build__modal-title">Unsaved changes</h3>
            <p className="report-template-build__modal-text">
              This template has changes that haven't been saved. Leave without saving?
            </p>
            <div className="report-template-build__modal-actions">
              <button type="button" className="report-template-build__back-button" onClick={() => setLeaveTarget(null)}>
                Keep Editing
              </button>
              <button
                type="button"
                className="report-template-build__delete-button report-template-build__delete-button--solid"
                onClick={confirmLeaveEditor}
              >
                Leave Without Saving
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
