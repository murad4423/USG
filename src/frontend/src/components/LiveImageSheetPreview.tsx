import { useCallback, useEffect, useLayoutEffect, useRef, useState } from "react";
import type { CSSProperties } from "react";
import type { ContentArea, TemplatePlaceholder } from "../ipcImageTemplate";
import { placeholderBoxStyle, placeholderTextStyle } from "../placeholderFields";

interface LiveImageSheetPreviewImage {
  sopInstanceUid: string;
  /** Browser-loadable URL for this image — same `thumbnailUrl` ThumbnailGrid already loaded, so it renders from cache instantly instead of fetching again. */
  url?: string;
}

interface LiveImageSheetPreviewProps {
  /** Selected images, in the order the operator selected them (not the study's original order). */
  images: LiveImageSheetPreviewImage[];
  /** Fixed (columns, rows) from the Grid Layout dropdown, or null for "Auto" (a near-square grid computed from the selection count). */
  layout: { columns: number; rows: number } | null;
  /**
   * "Single Page" toggle.
   *  - ticked:   every sheet is scaled so a WHOLE A4 page fits in the panel
   *              (nothing cut off). With several sheets they are stacked and
   *              you scroll to reach the next one (snaps page by page).
   *  - unticked: sheets fill the full panel WIDTH (bigger); scroll to see the
   *              rest of the page(s).
   * In both modes Ctrl + mouse wheel (or the − / + buttons) zooms the sheet;
   * 100% = the mode's normal size, and Reset returns to it.
   */
  fitSinglePage?: boolean;
  /**
   * Settings screen's uploaded "Image Template" background (its cached,
   * rasterized first page — see ImageTemplateBackground on the host side),
   * or undefined if none is uploaded. Drawn behind every sheet, edge to
   * edge, with the image grid on top — purely a CSS background-image, so
   * toggling/selecting images never touches this and stays instant. The
   * real, pixel-exact PDF composed at print time (ImageSheetComposer) draws
   * the same template the same way, so what's shown here matches print.
   */
  backgroundImageUrl?: string;
  /**
   * The "image area" chosen in Settings (Select Image Area), as fractions
   * of the page. When given together with backgroundImageUrl, the image
   * grid is confined to exactly that rectangle — so the template's own
   * header/footer around it stay visible — instead of spanning the whole
   * page. The printed PDF (ImageSheetComposer) uses the same rectangle.
   */
  contentArea?: ContentArea | null;
  /**
   * "Gray" option. Only used together with contentArea: true draws each
   * image on a light-grey box with a thin border (and the printed PDF does
   * exactly the same); false shows the images clean, with nothing behind
   * or around them.
   */
  grayBackground?: boolean;
  /**
   * Patient-information boxes the operator placed on the template in
   * Settings (Select Image Area), and the text each field prints for the
   * current patient (fieldKey -> value, see buildPlaceholderValues). Drawn
   * on top of every sheet, only when a template background is shown — the
   * printed PDF (ImageSheetComposer) draws the same boxes the same way.
   */
  placeholders?: TemplatePlaceholder[];
  placeholderValues?: Record<string, string>;
}

/** A4 portrait — matches ImageSheetComposer's default page size. */
const PAGE_ASPECT = 210 / 297;
const MIN_ZOOM = 0.25;
const MAX_ZOOM = 4;
const WHEEL_SENSITIVITY = 0.0012;

/** Same near-square grid formula as the host's ImageSheetComposer.CalculateGridDimensions, so "Auto" here looks like "Auto" there. */
function computeAutoGrid(imageCount: number): { columns: number; rows: number } {
  const columns = Math.max(1, Math.ceil(Math.sqrt(imageCount)));
  const rows = Math.max(1, Math.ceil(imageCount / columns));
  return { columns, rows };
}

/** Largest A4-shaped box that fits completely inside the given area. */
function fitPageToStage(availableWidth: number, availableHeight: number): { width: number; height: number } {
  if (availableWidth <= 0 || availableHeight <= 0) {
    return { width: 0, height: 0 };
  }
  let height = availableHeight;
  let width = height * PAGE_ASPECT;
  if (width > availableWidth) {
    width = availableWidth;
    height = width / PAGE_ASPECT;
  }
  return { width: Math.floor(width), height: Math.floor(height) };
}

/** A4-shaped box that uses the full available width (height follows, so it may be taller than the panel). */
function fitPageToWidth(availableWidth: number): { width: number; height: number } {
  if (availableWidth <= 0) {
    return { width: 0, height: 0 };
  }
  return { width: Math.floor(availableWidth), height: Math.floor(availableWidth / PAGE_ASPECT) };
}

function clampZoom(value: number): number {
  return Math.min(MAX_ZOOM, Math.max(MIN_ZOOM, Math.round(value * 1000) / 1000));
}

/** Keeps the point under the cursor (or the panel centre) steady while zooming. */
interface ZoomAnchor {
  mx: number;
  my: number;
  fx: number;
  fy: number;
}

/**
 * Instant, client-only preview of the selected images arranged into the
 * chosen print grid — no IPC round-trip, no PDF composition. Each sheet is
 * drawn as a real A4 page. The real, pixel-exact PDF is still composed
 * server-side (ImageSheetComposer) at print time, so printing is unaffected
 * by anything here (including zoom).
 */
export default function LiveImageSheetPreview({
  images,
  layout,
  fitSinglePage = false,
  backgroundImageUrl,
  contentArea,
  grayBackground = false,
  placeholders,
  placeholderValues
}: LiveImageSheetPreviewProps) {
  const viewportRef = useRef<HTMLDivElement>(null);
  const stageRef = useRef<HTMLDivElement>(null);
  const anchorRef = useRef<ZoomAnchor | null>(null);
  const previousPageCountRef = useRef(0);
  const [viewportSize, setViewportSize] = useState({ width: 0, height: 0, gutter: 0 });
  const [zoom, setZoom] = useState(1);

  // Measure the fixed outer viewport (its size does not depend on whether
  // scrollbars are showing, so zooming can never make the layout flicker).
  useLayoutEffect(() => {
    const viewport = viewportRef.current;
    const stage = stageRef.current;
    if (!viewport || !stage) {
      return;
    }
    const update = () =>
      setViewportSize({
        width: viewport.clientWidth,
        height: viewport.clientHeight,
        // Scrollbar thickness (0 for overlay scrollbars). Reserved so that
        // at 100% no scrollbar is ever needed just because of the scrollbar itself.
        gutter: Math.max(0, stage.offsetWidth - stage.clientWidth)
      });
    update();
    const observer = new ResizeObserver(update);
    observer.observe(viewport);
    return () => observer.disconnect();
  }, []);

  const applyZoom = useCallback((compute: (current: number) => number, focus?: { x: number; y: number }) => {
    const stage = stageRef.current;
    if (stage) {
      const mx = focus?.x ?? stage.clientWidth / 2;
      const my = focus?.y ?? stage.clientHeight / 2;
      anchorRef.current = {
        mx,
        my,
        fx: (stage.scrollLeft + mx) / Math.max(1, stage.scrollWidth),
        fy: (stage.scrollTop + my) / Math.max(1, stage.scrollHeight)
      };
    }
    setZoom((current) => clampZoom(compute(current)));
  }, []);

  // Ctrl + wheel = zoom the sheet (and stop WebView2 zooming the whole app
  // while the mouse is over the preview). Needs a non-passive native listener.
  useEffect(() => {
    const stage = stageRef.current;
    if (!stage) {
      return;
    }
    const onWheel = (event: WheelEvent) => {
      if (!event.ctrlKey) {
        return;
      }
      event.preventDefault();
      const rect = stage.getBoundingClientRect();
      applyZoom((current) => current * Math.exp(-event.deltaY * WHEEL_SENSITIVITY), {
        x: event.clientX - rect.left,
        y: event.clientY - rect.top
      });
    };
    stage.addEventListener("wheel", onWheel, { passive: false });
    return () => stage.removeEventListener("wheel", onWheel);
  }, [applyZoom]);

  // After the page has been resized for the new zoom, restore the scroll
  // position so the same spot stays under the cursor.
  useLayoutEffect(() => {
    const anchor = anchorRef.current;
    const stage = stageRef.current;
    if (!anchor || !stage) {
      return;
    }
    anchorRef.current = null;
    stage.scrollLeft = anchor.fx * stage.scrollWidth - anchor.mx;
    stage.scrollTop = anchor.fy * stage.scrollHeight - anchor.my;
  }, [zoom]);

  // Switching Single Page on/off changes what "100%" means, so start over at 100%.
  useEffect(() => {
    setZoom(1);
  }, [fitSinglePage]);

  const effectiveLayout = layout ?? computeAutoGrid(Math.max(1, images.length));
  const imagesPerPage = Math.max(1, effectiveLayout.columns * effectiveLayout.rows);

  const pages: LiveImageSheetPreviewImage[][] = [];
  for (let i = 0; i < images.length; i += imagesPerPage) {
    pages.push(images.slice(i, i + imagesPerPage));
  }
  const pageCount = pages.length;

  // With an image area (and a template behind it) the grid is placed at
  // exactly that rectangle of the page instead of filling the page's padding box.
  const areaStyle: CSSProperties | undefined =
    backgroundImageUrl && contentArea
      ? {
          position: "absolute",
          left: `${contentArea.x * 100}%`,
          top: `${contentArea.y * 100}%`,
          width: `${contentArea.width * 100}%`,
          height: `${contentArea.height * 100}%`
        }
      : undefined;

  // When a new sheet appears because more images were selected, scroll down to it.
  useEffect(() => {
    if (pageCount > previousPageCountRef.current && previousPageCountRef.current > 0) {
      const stage = stageRef.current;
      if (stage) {
        requestAnimationFrame(() => stage.scrollTo({ top: stage.scrollHeight, behavior: "smooth" }));
      }
    }
    previousPageCountRef.current = pageCount;
  }, [pageCount]);

  const padding = 4;
  const availableWidth = viewportSize.width - viewportSize.gutter - padding;
  const availableHeight = viewportSize.height - viewportSize.gutter - padding;
  const base = fitSinglePage ? fitPageToStage(availableWidth, availableHeight) : fitPageToWidth(availableWidth);
  const pageWidth = Math.floor(base.width * zoom);
  const pageHeight = Math.floor(base.height * zoom);

  const isDefaultZoom = Math.abs(zoom - 1) < 0.005;
  const snapPages = fitSinglePage && isDefaultZoom && pageCount > 1;

  const stageClassName = [
    "sheet-preview__stage",
    fitSinglePage ? "sheet-preview__stage--fit" : "sheet-preview__stage--wide",
    snapPages ? "sheet-preview__stage--snap" : ""
  ]
    .filter(Boolean)
    .join(" ");

  return (
    <div className="sheet-preview">
      <div className="sheet-preview__viewport" ref={viewportRef}>
        <div ref={stageRef} className={stageClassName}>
          {images.length === 0 ? (
            <div className="sheet-preview__placeholder">🖼️ Select at least one image to see a preview.</div>
          ) : (
            pages.map((pageImages, index) => (
              <div
                className="sheet-preview__page"
                key={index}
                style={{
                  width: pageWidth,
                  height: pageHeight,
                  ...(backgroundImageUrl
                    ? {
                        backgroundImage: `url("${backgroundImageUrl}")`,
                        backgroundSize: "100% 100%",
                        backgroundRepeat: "no-repeat",
                        backgroundPosition: "center"
                      }
                    : undefined)
                }}
              >
                <div
                  className="sheet-preview__grid"
                  style={{
                    gridTemplateColumns: `repeat(${effectiveLayout.columns}, minmax(0, 1fr))`,
                    gridTemplateRows: `repeat(${effectiveLayout.rows}, minmax(0, 1fr))`,
                    ...areaStyle
                  }}
                >
                  {pageImages.map((image) => (
                    <div
                      className={`sheet-preview__cell${
                        areaStyle ? (grayBackground ? " sheet-preview__cell--gray" : " sheet-preview__cell--plain") : ""
                      }`}
                      key={image.sopInstanceUid}
                    >
                      {image.url ? (
                        <img src={image.url} alt="" />
                      ) : (
                        <span className="sheet-preview__cell-fallback">No preview</span>
                      )}
                    </div>
                  ))}
                </div>
                {backgroundImageUrl &&
                  placeholders?.map((item) => {
                    const value = placeholderValues?.[item.field]?.trim();
                    if (!value) {
                      return null;
                    }
                    return (
                      <div key={item.id} style={{ ...placeholderBoxStyle(item), pointerEvents: "none" }}>
                        <div style={placeholderTextStyle(item, pageHeight)}>{value}</div>
                      </div>
                    );
                  })}
                {pageCount > 1 && (
                  <span className="sheet-preview__page-badge">
                    {index + 1} / {pageCount}
                  </span>
                )}
              </div>
            ))
          )}
        </div>
      </div>

      <div className="sheet-preview__zoombar">
        <button
          type="button"
          title="Zoom out"
          onClick={() => applyZoom((current) => Math.round(current * 10) / 10 - 0.1)}
          disabled={images.length === 0 || zoom <= MIN_ZOOM}
        >
          −
        </button>
        <span className="sheet-preview__zoom-value" title="100% = normal size for the current mode">
          Zoom {Math.round(zoom * 100)}%
        </span>
        <button
          type="button"
          title="Zoom in"
          onClick={() => applyZoom((current) => Math.round(current * 10) / 10 + 0.1)}
          disabled={images.length === 0 || zoom >= MAX_ZOOM}
        >
          +
        </button>
        <button
          type="button"
          className="sheet-preview__zoom-reset"
          title="Back to 100%"
          onClick={() => applyZoom(() => 1)}
          disabled={isDefaultZoom}
        >
          Reset
        </button>
        <span className="sheet-preview__zoom-hint">Ctrl + scroll to zoom</span>
      </div>
    </div>
  );
}
