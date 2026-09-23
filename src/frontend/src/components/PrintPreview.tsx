import { Spinner } from "./StateMessage";

interface PrintPreviewProps {
  previewUrl?: string;
  pageCount?: number;
  imagesComposed?: number;
  skippedCount?: number;
  columns?: number;
  rows?: number;
  loading: boolean;
  error?: string | null;
}

/**
 * Shows the composed image-sheet PDF before the user commits to printing
 * it. `previewUrl` is a "https://printpreview/..." URL — WebView2 renders
 * PDFs natively, so an <iframe> is enough; no PDF.js or similar needed.
 */
export default function PrintPreview({
  previewUrl,
  pageCount,
  imagesComposed,
  skippedCount,
  columns,
  rows,
  loading,
  error
}: PrintPreviewProps) {
  return (
    <div className="print-preview">
      <div className="print-preview__summary">
        {loading && <Spinner label="Composing preview..." />}

        {!loading && error && <span className="print-preview__error">⚠️ {error}</span>}

        {!loading && !error && previewUrl && (
          <span>
            {imagesComposed} image{imagesComposed === 1 ? "" : "s"}
            {columns && rows ? ` • ${columns}×${rows} grid` : ""}
            {pageCount ? ` • ${pageCount} page${pageCount === 1 ? "" : "s"}` : ""}
            {skippedCount ? ` • ${skippedCount} skipped (missing on disk)` : ""}
          </span>
        )}

        {!loading && !error && !previewUrl && <span>Select at least one image to see a preview.</span>}
      </div>

      <div className="print-preview__frame-wrapper">
        {previewUrl ? (
          <iframe className="print-preview__frame" title="Print preview" src={previewUrl} />
        ) : (
          <div className="print-preview__placeholder">
            {loading ? <Spinner label="Composing preview..." /> : <span>🖼️ No preview yet</span>}
          </div>
        )}
      </div>
    </div>
  );
}
