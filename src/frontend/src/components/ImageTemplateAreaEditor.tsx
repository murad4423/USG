import { useEffect, useLayoutEffect, useRef, useState } from "react";
import type { PointerEvent as ReactPointerEvent } from "react";
import type { ContentArea, TemplatePlaceholder } from "../ipcImageTemplate";
import {
  DEFAULT_PLACEHOLDER_COLOR,
  DEFAULT_PLACEHOLDER_FONT_POINTS,
  MAX_FONT_POINTS,
  MIN_FONT_POINTS,
  PLACEHOLDER_FIELDS,
  PLACEHOLDER_FONT_FAMILIES,
  effectiveFontPoints,
  fieldLabel,
  fieldSample,
  minBoxHeightForFont,
  placeholderBoxStyle,
  placeholderTextStyle,
  validColor
} from "../placeholderFields";
import "../styles/template-area-editor.css";

interface ImageTemplateAreaEditorProps {
  /** The template's first page as an image (same one the print preview uses as its background). */
  backgroundImageUrl: string;
  /** The currently saved area, or null if none has been selected yet. */
  initialArea: ContentArea | null;
  /** The currently saved patient-information placeholders (may be empty). */
  initialPlaceholders: TemplatePlaceholder[];
  saving: boolean;
  /** Error from the last save attempt, shown next to the buttons. */
  error?: string | null;
  /**
   * Called with the chosen image area (or null if the operator cleared it —
   * back to normal page margins) and the full list of placeholders.
   */
  onSave: (area: ContentArea | null, placeholders: TemplatePlaceholder[]) => void;
  onCancel: () => void;
}

type ResizeHandle = "nw" | "n" | "ne" | "e" | "se" | "s" | "sw" | "w";

const HANDLES: ResizeHandle[] = ["nw", "n", "ne", "e", "se", "s", "sw", "w"];

/** Same limit the host enforces (ImageTemplateContentArea.MinimumSize). */
const MIN_SIZE = 0.05;

/** Smallest placeholder box (page fractions). The host accepts anything at or above 0.02 x 0.008. */
const PLACEHOLDER_MIN_WIDTH = 0.03;
const PLACEHOLDER_MIN_HEIGHT = 0.012;

/** Zoom range: 1 = the whole page fits in the window ("Fit"). */
const ZOOM_MIN = 1;
const ZOOM_MAX = 8;
const ZOOM_BUTTON_FACTOR = 1.25;

/** Space around the page inside the scrolling area (must match .tae-body padding). */
const BODY_PADDING = 14;

const COLOR_PRESETS = ["#000000", "#1c3d7a", "#c92a2a", "#495057", "#ffffff"];

interface Rect {
  x: number;
  y: number;
  width: number;
  height: number;
}

type Selection = { kind: "area" } | { kind: "placeholder"; id: string } | null;

type DragState =
  | { mode: "draw"; startX: number; startY: number; previous: ContentArea | null; drawn: ContentArea | null }
  | { mode: "move"; startX: number; startY: number; origin: ContentArea }
  | { mode: "resize"; handle: ResizeHandle; origin: ContentArea }
  | { mode: "phMove"; id: string; startX: number; startY: number; origin: TemplatePlaceholder }
  | { mode: "phResize"; id: string; handle: ResizeHandle; origin: TemplatePlaceholder }
  | { mode: "pan"; clientX: number; clientY: number; scrollLeft: number; scrollTop: number };

function clamp(value: number, min: number, max: number): number {
  return Math.min(max, Math.max(min, value));
}

/** "11" / "11.5" — no trailing zeros. */
function formatPoints(value: number): string {
  return String(Math.round(value * 10) / 10);
}

/** Moves a rectangle by (dx, dy), keeping it inside the page. */
function moveRect<T extends Rect>(origin: T, dx: number, dy: number): T {
  return {
    ...origin,
    x: clamp(origin.x + dx, 0, 1 - origin.width),
    y: clamp(origin.y + dy, 0, 1 - origin.height)
  };
}

/** Drags one handle of a rectangle to `point`, keeping it inside the page and at least minWidth x minHeight. */
function resizeRect<T extends Rect>(
  origin: T,
  handle: ResizeHandle,
  point: { x: number; y: number },
  minWidth: number,
  minHeight: number
): T {
  let left = origin.x;
  let right = origin.x + origin.width;
  let top = origin.y;
  let bottom = origin.y + origin.height;

  if (handle.includes("w")) {
    left = clamp(point.x, 0, right - minWidth);
  }
  if (handle.includes("e")) {
    right = clamp(point.x, left + minWidth, 1);
  }
  if (handle.includes("n")) {
    top = clamp(point.y, 0, bottom - minHeight);
  }
  if (handle.includes("s")) {
    bottom = clamp(point.y, top + minHeight, 1);
  }
  return { ...origin, x: left, y: top, width: right - left, height: bottom - top };
}

function newPlaceholderId(): string {
  return `ph-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 7)}`;
}

/** Placeholders saved by the first version have no font size — make it explicit so the editor can show/change it. */
function withExplicitFontSize(item: TemplatePlaceholder): TemplatePlaceholder {
  if (item.fontSize !== undefined && item.fontSize > 0) {
    return item;
  }
  return { ...item, fontSize: Math.round(effectiveFontPoints(item) * 2) / 2 };
}

interface NumberFieldProps {
  value: number;
  min: number;
  max: number;
  step: number;
  disabled?: boolean;
  ariaLabel: string;
  onCommit: (value: number) => void;
}

/**
 * Number input that lets the operator type freely ("1" on the way to "12"
 * doesn't get clamped mid-typing): a value is applied as soon as it is inside
 * min..max, and clamped when the field loses focus.
 */
function NumberField({ value, min, max, step, disabled, ariaLabel, onCommit }: NumberFieldProps) {
  const [draft, setDraft] = useState(formatPoints(value));
  const [focused, setFocused] = useState(false);

  useEffect(() => {
    if (!focused) {
      setDraft(formatPoints(value));
    }
  }, [value, focused]);

  return (
    <input
      type="number"
      className="tae-number"
      aria-label={ariaLabel}
      min={min}
      max={max}
      step={step}
      value={draft}
      disabled={disabled}
      onFocus={() => setFocused(true)}
      onChange={(event) => {
        setDraft(event.target.value);
        const parsed = Number.parseFloat(event.target.value);
        if (Number.isFinite(parsed) && parsed >= min && parsed <= max) {
          onCommit(parsed);
        }
      }}
      onBlur={() => {
        setFocused(false);
        const parsed = Number.parseFloat(draft);
        const finalValue = Number.isFinite(parsed) ? clamp(parsed, min, max) : value;
        setDraft(formatPoints(finalValue));
        onCommit(finalValue);
      }}
      onKeyDown={(event) => {
        if (event.key === "Enter") {
          event.currentTarget.blur();
        }
      }}
    />
  );
}

/**
 * Full-screen dialog ("Select Image Area") for laying out the uploaded
 * template page. Two things can be placed on it:
 *
 *  1. The IMAGE AREA — the rectangle where the selected ultrasound images
 *     are printed. Drag on empty template space to draw it, drag inside it
 *     to move it, drag its square handles to resize it. Everything outside
 *     it (the hospital's header/footer) is dimmed here and stays untouched
 *     when printing.
 *  2. PATIENT INFORMATION PLACEHOLDERS — pick a field (name, ID, age, sex...)
 *     from the "Patient information" dropdown and a box for it appears on
 *     the template. Drag the box to move it, drag its handles to resize it.
 *     The text's font, size (in pt), colour, bold and alignment are set from
 *     the second toolbar row. When an image sheet is previewed/printed for a
 *     patient, each box is filled with that patient's value.
 *
 * The page can be ZOOMED (buttons, slider or Ctrl + mouse wheel) so small
 * boxes can be placed precisely; scroll with the wheel/scrollbars, or pan
 * with the middle mouse button or Space + drag.
 *
 * Everything is reported as fractions of the page (0..1), so it works at any
 * size.
 */
export default function ImageTemplateAreaEditor({
  backgroundImageUrl,
  initialArea,
  initialPlaceholders,
  saving,
  error,
  onSave,
  onCancel
}: ImageTemplateAreaEditorProps) {
  const bodyRef = useRef<HTMLDivElement>(null);
  const canvasRef = useRef<HTMLDivElement>(null);
  const dragRef = useRef<DragState | null>(null);
  const zoomRef = useRef(1);
  /** Where on the page the operator was looking when zooming, so it can be kept under the cursor afterwards. */
  const zoomAnchorRef = useRef<{ x: number; y: number; fx: number; fy: number } | null>(null);
  const spaceDownRef = useRef(false);

  const [area, setArea] = useState<ContentArea | null>(initialArea);
  const [placeholders, setPlaceholders] = useState<TemplatePlaceholder[]>(() =>
    initialPlaceholders.map(withExplicitFontSize)
  );
  const [selection, setSelection] = useState<Selection>(initialArea ? { kind: "area" } : null);
  const [imageFailed, setImageFailed] = useState(false);
  const [zoom, setZoom] = useState(1);
  const [spaceDown, setSpaceDown] = useState(false);
  const [panning, setPanning] = useState(false);
  /** Size of the scrolling area and the template's aspect ratio: together they give the "fit" size. */
  const [bodySize, setBodySize] = useState({ width: 0, height: 0 });
  const [aspect, setAspect] = useState(0);
  /** On-screen height of the template image, so the sample text can be drawn at the real relative size. */
  const [canvasHeight, setCanvasHeight] = useState(0);

  // Width at which the whole page just fits the window; the canvas is that x zoom.
  const fitWidth =
    aspect > 0 && bodySize.width > 0 && bodySize.height > 0
      ? Math.max(
          120,
          Math.floor(
            Math.min(bodySize.width - BODY_PADDING * 2, (bodySize.height - BODY_PADDING * 2) * aspect)
          ) - 2
        )
      : 0;
  const canvasWidth = fitWidth > 0 ? fitWidth * zoom : 320;

  zoomRef.current = zoom;

  useEffect(() => {
    const body = bodyRef.current;
    if (!body) {
      return;
    }
    const update = () => setBodySize({ width: body.clientWidth, height: body.clientHeight });
    update();
    const observer = new ResizeObserver(update);
    observer.observe(body);
    return () => observer.disconnect();
  }, [imageFailed]);

  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas) {
      return;
    }
    const update = () => setCanvasHeight(canvas.clientHeight);
    update();
    const observer = new ResizeObserver(update);
    observer.observe(canvas);
    return () => observer.disconnect();
  }, [imageFailed]);

  /** Sets the zoom, keeping the page point under `anchor` (client coords; default: window centre) where it was. */
  function applyZoom(next: number, anchor?: { x: number; y: number }) {
    const clamped = clamp(Math.round(next * 1000) / 1000, ZOOM_MIN, ZOOM_MAX);
    if (clamped === zoomRef.current) {
      return;
    }
    const body = bodyRef.current;
    const canvas = canvasRef.current;
    if (body && canvas) {
      const bodyRect = body.getBoundingClientRect();
      const canvasRect = canvas.getBoundingClientRect();
      const ax = anchor?.x ?? bodyRect.left + bodyRect.width / 2;
      const ay = anchor?.y ?? bodyRect.top + bodyRect.height / 2;
      zoomAnchorRef.current = {
        x: ax,
        y: ay,
        fx: (ax - canvasRect.left) / canvasRect.width,
        fy: (ay - canvasRect.top) / canvasRect.height
      };
    }
    zoomRef.current = clamped;
    setZoom(clamped);
  }

  // After the canvas has been resized for the new zoom, scroll so the anchor point is back under the cursor.
  useLayoutEffect(() => {
    const anchor = zoomAnchorRef.current;
    zoomAnchorRef.current = null;
    const body = bodyRef.current;
    const canvas = canvasRef.current;
    if (!anchor || !body || !canvas) {
      return;
    }
    const rect = canvas.getBoundingClientRect();
    body.scrollLeft += rect.left + anchor.fx * rect.width - anchor.x;
    body.scrollTop += rect.top + anchor.fy * rect.height - anchor.y;
  }, [zoom, fitWidth]);

  // Ctrl + mouse wheel (or a trackpad pinch) zooms towards the pointer; a plain wheel scrolls as usual.
  useEffect(() => {
    const body = bodyRef.current;
    if (!body) {
      return;
    }
    const onWheel = (event: WheelEvent) => {
      if (!event.ctrlKey) {
        return;
      }
      event.preventDefault();
      applyZoom(zoomRef.current * Math.exp(-event.deltaY * 0.0015), { x: event.clientX, y: event.clientY });
    };
    body.addEventListener("wheel", onWheel, { passive: false });
    return () => body.removeEventListener("wheel", onWheel);
  }, [imageFailed]);

  // Holding Space turns the mouse into a "hand" that pans the zoomed page.
  useEffect(() => {
    const isFormField = (target: EventTarget | null) =>
      target instanceof HTMLInputElement || target instanceof HTMLSelectElement || target instanceof HTMLTextAreaElement;
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.code !== "Space" || isFormField(event.target)) {
        return;
      }
      event.preventDefault();
      spaceDownRef.current = true;
      setSpaceDown(true);
    };
    const onKeyUp = (event: KeyboardEvent) => {
      if (event.code !== "Space") {
        return;
      }
      if (!isFormField(event.target)) {
        event.preventDefault();
      }
      spaceDownRef.current = false;
      setSpaceDown(false);
    };
    const onBlur = () => {
      spaceDownRef.current = false;
      setSpaceDown(false);
    };
    window.addEventListener("keydown", onKeyDown);
    window.addEventListener("keyup", onKeyUp);
    window.addEventListener("blur", onBlur);
    return () => {
      window.removeEventListener("keydown", onKeyDown);
      window.removeEventListener("keyup", onKeyUp);
      window.removeEventListener("blur", onBlur);
    };
  }, []);

  const selectedPlaceholder =
    selection?.kind === "placeholder" ? placeholders.find((item) => item.id === selection.id) ?? null : null;

  function updatePlaceholder(id: string, patch: Partial<TemplatePlaceholder>) {
    setPlaceholders((current) => current.map((item) => (item.id === id ? { ...item, ...patch } : item)));
  }

  /** Changes the font size; the box grows if it would be too short for the new size. */
  function setFontSize(id: string, points: number) {
    const size = clamp(Math.round(points * 2) / 2, MIN_FONT_POINTS, MAX_FONT_POINTS);
    setPlaceholders((current) =>
      current.map((item) => {
        if (item.id !== id) {
          return item;
        }
        const height = Math.min(1, Math.max(item.height, minBoxHeightForFont(size)));
        return { ...item, fontSize: size, height, y: Math.min(item.y, 1 - height) };
      })
    );
  }

  function removePlaceholder(id: string) {
    setPlaceholders((current) => current.filter((item) => item.id !== id));
    setSelection(null);
  }

  // Delete key removes the selected placeholder (unless the operator is typing in / using a form control).
  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key !== "Delete" || selection?.kind !== "placeholder") {
        return;
      }
      const target = event.target;
      if (target instanceof HTMLInputElement || target instanceof HTMLSelectElement || target instanceof HTMLTextAreaElement) {
        return;
      }
      event.preventDefault();
      removePlaceholder(selection.id);
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [selection]);

  // Choosing a field in the dropdown drops a new box for it into the part of the page the operator is looking at.
  function addPlaceholder(field: string) {
    // The visible part of the page, as fractions of the page.
    let centerX = 0.3;
    let centerY = 0.08;
    let visibleWidth = 1;
    const body = bodyRef.current;
    const canvas = canvasRef.current;
    if (body && canvas) {
      const bodyRect = body.getBoundingClientRect();
      const canvasRect = canvas.getBoundingClientRect();
      const left = Math.max(bodyRect.left, canvasRect.left);
      const right = Math.min(bodyRect.right, canvasRect.right);
      const top = Math.max(bodyRect.top, canvasRect.top);
      const bottom = Math.min(bodyRect.bottom, canvasRect.bottom);
      if (right > left && bottom > top && canvasRect.width > 0 && canvasRect.height > 0) {
        centerX = ((left + right) / 2 - canvasRect.left) / canvasRect.width;
        centerY = ((top + bottom) / 2 - canvasRect.top) / canvasRect.height;
        visibleWidth = (right - left) / canvasRect.width;
      }
    }

    const fontSize = DEFAULT_PLACEHOLDER_FONT_POINTS;
    const width = clamp(visibleWidth * 0.6, 0.08, 0.3);
    const height = Math.max(0.022, minBoxHeightForFont(fontSize));
    const stagger = (placeholders.length % 5) * 0.012;
    const created: TemplatePlaceholder = {
      id: newPlaceholderId(),
      field,
      x: clamp(centerX - width / 2 + stagger, 0, 1 - width),
      y: clamp(centerY - height / 2 + stagger, 0, 1 - height),
      width,
      height,
      bold: false,
      align: "left",
      fontSize,
      fontFamily: "",
      color: DEFAULT_PLACEHOLDER_COLOR
    };
    setPlaceholders((current) => [...current, created]);
    setSelection({ kind: "placeholder", id: created.id });
  }

  /** Pointer position as a 0..1 fraction of the template image. */
  function pointFrom(event: ReactPointerEvent): { x: number; y: number } {
    const rect = canvasRef.current?.getBoundingClientRect();
    if (!rect || rect.width === 0 || rect.height === 0) {
      return { x: 0, y: 0 };
    }
    return {
      x: clamp((event.clientX - rect.left) / rect.width, 0, 1),
      y: clamp((event.clientY - rect.top) / rect.height, 0, 1)
    };
  }

  function beginDrag(event: ReactPointerEvent, state: DragState) {
    canvasRef.current?.setPointerCapture(event.pointerId);
    dragRef.current = state;
  }

  /** Middle mouse button, or Space + left button, pans the zoomed page. Returns true if a pan started. */
  function tryBeginPan(event: ReactPointerEvent): boolean {
    const wantsPan = event.button === 1 || (event.button === 0 && spaceDownRef.current);
    const body = bodyRef.current;
    if (!wantsPan || !body) {
      return false;
    }
    event.preventDefault();
    setPanning(true);
    beginDrag(event, {
      mode: "pan",
      clientX: event.clientX,
      clientY: event.clientY,
      scrollLeft: body.scrollLeft,
      scrollTop: body.scrollTop
    });
    return true;
  }

  // Drag on empty template space: draw a new image-area rectangle.
  function handleCanvasPointerDown(event: ReactPointerEvent<HTMLDivElement>) {
    if (tryBeginPan(event) || event.button !== 0) {
      return;
    }
    const point = pointFrom(event);
    setSelection({ kind: "area" });
    beginDrag(event, { mode: "draw", startX: point.x, startY: point.y, previous: area, drawn: null });
  }

  // Drag inside the image area: select + move it.
  function handleAreaPointerDown(event: ReactPointerEvent<HTMLDivElement>) {
    if (event.button !== 0 || !area || spaceDownRef.current) {
      return; // Space / middle button: let the canvas start a pan
    }
    event.stopPropagation();
    setSelection({ kind: "area" });
    const point = pointFrom(event);
    beginDrag(event, { mode: "move", startX: point.x, startY: point.y, origin: area });
  }

  // Drag a square handle of the image area: resize.
  function handleAreaHandlePointerDown(event: ReactPointerEvent<HTMLDivElement>, handle: ResizeHandle) {
    if (event.button !== 0 || !area || spaceDownRef.current) {
      return;
    }
    event.stopPropagation();
    beginDrag(event, { mode: "resize", handle, origin: area });
  }

  // Drag inside a placeholder box: select + move it.
  function handlePlaceholderPointerDown(event: ReactPointerEvent<HTMLDivElement>, item: TemplatePlaceholder) {
    if (event.button !== 0 || spaceDownRef.current) {
      return;
    }
    event.stopPropagation();
    setSelection({ kind: "placeholder", id: item.id });
    const point = pointFrom(event);
    beginDrag(event, { mode: "phMove", id: item.id, startX: point.x, startY: point.y, origin: item });
  }

  // Drag a square handle of a placeholder box: resize.
  function handlePlaceholderHandlePointerDown(
    event: ReactPointerEvent<HTMLDivElement>,
    item: TemplatePlaceholder,
    handle: ResizeHandle
  ) {
    if (event.button !== 0 || spaceDownRef.current) {
      return;
    }
    event.stopPropagation();
    beginDrag(event, { mode: "phResize", id: item.id, handle, origin: item });
  }

  function handlePointerMove(event: ReactPointerEvent<HTMLDivElement>) {
    const drag = dragRef.current;
    if (!drag) {
      return;
    }

    if (drag.mode === "pan") {
      const body = bodyRef.current;
      if (body) {
        body.scrollLeft = drag.scrollLeft - (event.clientX - drag.clientX);
        body.scrollTop = drag.scrollTop - (event.clientY - drag.clientY);
      }
      return;
    }

    const point = pointFrom(event);

    switch (drag.mode) {
      case "draw":
        drag.drawn = {
          x: Math.min(drag.startX, point.x),
          y: Math.min(drag.startY, point.y),
          width: Math.abs(point.x - drag.startX),
          height: Math.abs(point.y - drag.startY)
        };
        setArea(drag.drawn);
        break;
      case "move":
        setArea(moveRect(drag.origin, point.x - drag.startX, point.y - drag.startY));
        break;
      case "resize":
        setArea(resizeRect(drag.origin, drag.handle, point, MIN_SIZE, MIN_SIZE));
        break;
      case "phMove":
        updatePlaceholder(drag.id, moveRect(drag.origin, point.x - drag.startX, point.y - drag.startY));
        break;
      case "phResize":
        updatePlaceholder(
          drag.id,
          resizeRect(drag.origin, drag.handle, point, PLACEHOLDER_MIN_WIDTH, PLACEHOLDER_MIN_HEIGHT)
        );
        break;
    }
  }

  function handlePointerUp(event: ReactPointerEvent<HTMLDivElement>) {
    const drag = dragRef.current;
    dragRef.current = null;
    if (canvasRef.current?.hasPointerCapture(event.pointerId)) {
      canvasRef.current.releasePointerCapture(event.pointerId);
    }
    setPanning(false);

    // A plain click (or a tiny accidental drag) while drawing must not
    // replace the existing rectangle with a speck — put the old one back
    // (and treat a plain click as "click on empty space": deselect).
    if (drag?.mode === "draw") {
      const drewUsableArea = drag.drawn !== null && drag.drawn.width >= MIN_SIZE && drag.drawn.height >= MIN_SIZE;
      if (!drewUsableArea) {
        setArea(drag.previous);
        setSelection(drag.drawn !== null && drag.previous ? { kind: "area" } : null);
      }
    }
  }

  const areaIsUsable = area !== null && area.width >= MIN_SIZE && area.height >= MIN_SIZE;

  const percent = (value: number) => `${(value * 100).toFixed(1)}%`;

  const selectedColor = selectedPlaceholder
    ? validColor(selectedPlaceholder.color) ?? DEFAULT_PLACEHOLDER_COLOR
    : DEFAULT_PLACEHOLDER_COLOR;
  const selectedFontPoints = selectedPlaceholder ? effectiveFontPoints(selectedPlaceholder) : DEFAULT_PLACEHOLDER_FONT_POINTS;

  return (
    <div className="tae-backdrop" role="dialog" aria-modal="true" aria-label="Select image area">
      <div className="tae-dialog">
        <div className="tae-header">
          <h2 className="tae-title">Select Image Area</h2>
          <p className="tae-help">
            <b>Images:</b> drag on the template to draw the area where the images are printed; drag the box to move
            it, drag its square handles to resize it. <b>Patient information:</b> pick a field from the dropdown — a
            box appears; move/resize it and set its font, size and colour. <b>Zoom:</b> Ctrl + mouse wheel, or the
            buttons. Move around with the scrollbars, the middle mouse button, or Space + drag.
          </p>
        </div>

        <div className="tae-toolbar">
          <label className="tae-toolbar__label" htmlFor="tae-add-field">
            Patient information
          </label>
          <select
            id="tae-add-field"
            className="tae-select"
            value=""
            disabled={saving}
            onChange={(event) => {
              if (event.target.value) {
                addPlaceholder(event.target.value);
              }
            }}
          >
            <option value="">＋ Add to template…</option>
            {PLACEHOLDER_FIELDS.map((field) => (
              <option key={field.key} value={field.key}>
                {field.label}
              </option>
            ))}
          </select>

          <div className="tae-zoom" role="group" aria-label="Zoom">
            <span className="tae-toolbar__label">Zoom</span>
            <button
              type="button"
              className="tae-zoom__button"
              title="Zoom out"
              disabled={zoom <= ZOOM_MIN}
              onClick={() => applyZoom(zoom / ZOOM_BUTTON_FACTOR)}
            >
              −
            </button>
            <input
              type="range"
              className="tae-zoom__slider"
              aria-label="Zoom level"
              min={ZOOM_MIN * 100}
              max={ZOOM_MAX * 100}
              step={10}
              value={Math.round(zoom * 100)}
              onChange={(event) => applyZoom(Number(event.target.value) / 100)}
            />
            <button
              type="button"
              className="tae-zoom__button"
              title="Zoom in"
              disabled={zoom >= ZOOM_MAX}
              onClick={() => applyZoom(zoom * ZOOM_BUTTON_FACTOR)}
            >
              +
            </button>
            <span className="tae-zoom__value">{Math.round(zoom * 100)}%</span>
            <button
              type="button"
              className="tae-zoom__button tae-zoom__button--wide"
              title="Show the whole page"
              disabled={zoom === ZOOM_MIN}
              onClick={() => applyZoom(ZOOM_MIN)}
            >
              Fit
            </button>
          </div>
        </div>

        <div className="tae-toolbar tae-toolbar--edit">
          {selectedPlaceholder ? (
            <>
              <span className="tae-toolbar__label">Selected box</span>
              <select
                className="tae-select"
                value={selectedPlaceholder.field}
                disabled={saving}
                onChange={(event) => updatePlaceholder(selectedPlaceholder.id, { field: event.target.value })}
                aria-label="Field shown in the selected box"
              >
                {PLACEHOLDER_FIELDS.map((field) => (
                  <option key={field.key} value={field.key}>
                    {field.label}
                  </option>
                ))}
              </select>

              <label className="tae-toolbar__label" htmlFor="tae-font-family">
                Font
              </label>
              <select
                id="tae-font-family"
                className="tae-select tae-select--font"
                value={selectedPlaceholder.fontFamily ?? ""}
                disabled={saving}
                onChange={(event) => updatePlaceholder(selectedPlaceholder.id, { fontFamily: event.target.value })}
              >
                {PLACEHOLDER_FONT_FAMILIES.map((font) => (
                  <option key={font.value} value={font.value}>
                    {font.label}
                  </option>
                ))}
              </select>

              <span className="tae-toolbar__label">Size</span>
              <NumberField
                value={selectedFontPoints}
                min={MIN_FONT_POINTS}
                max={MAX_FONT_POINTS}
                step={0.5}
                disabled={saving}
                ariaLabel="Font size in points"
                onCommit={(points) => setFontSize(selectedPlaceholder.id, points)}
              />
              <span className="tae-toolbar__unit">pt</span>

              <span className="tae-toolbar__label">Color</span>
              <input
                type="color"
                className="tae-color"
                aria-label="Text colour"
                value={selectedColor}
                disabled={saving}
                onChange={(event) => updatePlaceholder(selectedPlaceholder.id, { color: event.target.value })}
              />
              {COLOR_PRESETS.map((preset) => (
                <button
                  key={preset}
                  type="button"
                  className={`tae-swatch${selectedColor === preset ? " tae-swatch--active" : ""}`}
                  style={{ background: preset }}
                  title={preset}
                  aria-label={`Colour ${preset}`}
                  disabled={saving}
                  onClick={() => updatePlaceholder(selectedPlaceholder.id, { color: preset })}
                />
              ))}

              <label className="tae-check">
                <input
                  type="checkbox"
                  checked={Boolean(selectedPlaceholder.bold)}
                  disabled={saving}
                  onChange={(event) => updatePlaceholder(selectedPlaceholder.id, { bold: event.target.checked })}
                />
                Bold
              </label>
              <div className="tae-segment" role="group" aria-label="Text alignment">
                {(["left", "center", "right"] as const).map((align) => (
                  <button
                    key={align}
                    type="button"
                    className={`tae-segment__button${
                      (selectedPlaceholder.align ?? "left") === align ? " tae-segment__button--active" : ""
                    }`}
                    disabled={saving}
                    onClick={() => updatePlaceholder(selectedPlaceholder.id, { align })}
                  >
                    {align === "left" ? "Left" : align === "center" ? "Center" : "Right"}
                  </button>
                ))}
              </div>
              <button
                type="button"
                className="tae-button tae-button--danger"
                disabled={saving}
                onClick={() => removePlaceholder(selectedPlaceholder.id)}
              >
                Delete box
              </button>
            </>
          ) : (
            <span className="tae-toolbar__hint">
              {placeholders.length > 0
                ? "Click a box on the template to change its field, font, size and colour, or to delete it."
                : "No patient information added yet — use the dropdown above."}
            </span>
          )}
        </div>

        <div ref={bodyRef} className="tae-body">
          {imageFailed ? (
            <p className="tae-status tae-status--error">Could not load the template image.</p>
          ) : (
            <div
              ref={canvasRef}
              className={`tae-canvas${spaceDown ? " tae-canvas--pan" : ""}${panning ? " tae-canvas--panning" : ""}`}
              style={{ width: `${canvasWidth}px` }}
              onPointerDown={handleCanvasPointerDown}
              onPointerMove={handlePointerMove}
              onPointerUp={handlePointerUp}
              onPointerCancel={handlePointerUp}
              onMouseDown={(event) => {
                if (event.button === 1) {
                  event.preventDefault(); // no browser auto-scroll on middle click
                }
              }}
            >
              <img
                className="tae-image"
                src={backgroundImageUrl}
                alt="Image template"
                draggable={false}
                onLoad={(event) => {
                  const image = event.currentTarget;
                  if (image.naturalWidth > 0 && image.naturalHeight > 0) {
                    setAspect(image.naturalWidth / image.naturalHeight);
                  }
                }}
                onError={() => setImageFailed(true)}
              />

              {area && (
                <div
                  className={`tae-area${selection?.kind === "area" ? " tae-area--selected" : ""}`}
                  style={{
                    left: `${area.x * 100}%`,
                    top: `${area.y * 100}%`,
                    width: `${area.width * 100}%`,
                    height: `${area.height * 100}%`
                  }}
                  onPointerDown={handleAreaPointerDown}
                >
                  <span className="tae-area__label">Images print here</span>
                  {selection?.kind === "area" &&
                    HANDLES.map((handle) => (
                      <div
                        key={handle}
                        className={`tae-handle tae-handle--${handle}`}
                        onPointerDown={(event) => handleAreaHandlePointerDown(event, handle)}
                      />
                    ))}
                </div>
              )}

              {placeholders.map((item) => {
                const isSelected = selection?.kind === "placeholder" && selection.id === item.id;
                return (
                  <div
                    key={item.id}
                    className={`tae-ph${isSelected ? " tae-ph--selected" : ""}`}
                    style={placeholderBoxStyle(item)}
                    title={`${fieldLabel(item.field)} · ${formatPoints(effectiveFontPoints(item))} pt`}
                    onPointerDown={(event) => handlePlaceholderPointerDown(event, item)}
                  >
                    <div className="tae-ph__text" style={placeholderTextStyle(item, canvasHeight)}>
                      {fieldSample(item.field)}
                    </div>
                    {isSelected && (
                      <span className={`tae-ph__tag${item.y < 0.04 ? " tae-ph__tag--below" : ""}`}>
                        {fieldLabel(item.field)} · {formatPoints(effectiveFontPoints(item))} pt
                      </span>
                    )}
                    {isSelected &&
                      HANDLES.map((handle) => (
                        <div
                          key={handle}
                          className={`tae-handle tae-handle--${handle} tae-handle--small`}
                          onPointerDown={(event) => handlePlaceholderHandlePointerDown(event, item, handle)}
                        />
                      ))}
                  </div>
                );
              })}
            </div>
          )}
        </div>

        <div className="tae-footer">
          <span className={`tae-status ${error ? "tae-status--error" : ""}`}>
            {error
              ? `⚠️ ${error}`
              : (area
                  ? `Area: left ${percent(area.x)}, top ${percent(area.y)}, width ${percent(area.width)}, height ${percent(area.height)}`
                  : "No image area selected — images will use the normal page margins.") +
                ` · ${placeholders.length} patient information box${placeholders.length === 1 ? "" : "es"}`}
          </span>
          <button type="button" className="tae-button" onClick={() => setArea(null)} disabled={saving || area === null}>
            Clear Area
          </button>
          <button type="button" className="tae-button" onClick={onCancel} disabled={saving}>
            Cancel
          </button>
          <button
            type="button"
            className="tae-button tae-button--primary"
            onClick={() => onSave(areaIsUsable ? area : null, placeholders)}
            disabled={saving || (area !== null && !areaIsUsable)}
          >
            {saving ? "Saving..." : "Save"}
          </button>
        </div>
      </div>
    </div>
  );
}
