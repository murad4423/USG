import { useEffect, useMemo, useState } from "react";
import ThumbnailGrid from "../components/ThumbnailGrid";
import PrintPreview from "../components/PrintPreview";
import StateMessage, { Spinner } from "../components/StateMessage";
import {
  composeImagePrintPreview,
  printImageSheet,
  type PatientStudyRecord
} from "../ipc";

interface ImagePrintPageProps {
  /** The study to print, loaded via PatientListPage's "Print Images" action. Null until one has been chosen. */
  study: PatientStudyRecord | null;
}

interface PreviewState {
  previewUrl?: string;
  pageCount?: number;
  imagesComposed?: number;
  skippedCount?: number;
  columns?: number;
  rows?: number;
}

type PrintStatus =
  | { kind: "idle" }
  | { kind: "printing" }
  | { kind: "result"; success: boolean; message: string };

/**
 * Image Print page: pick a subset of the loaded study's images
 * (ThumbnailGrid), see the composed sheet before committing
 * (PrintPreview), then send it to SilentPrinter. Report printing is a
 * separate, not-yet-built flow — this page only ever touches
 * composeImagePrintPreview / printImageSheet.
 */
export default function ImagePrintPage({ study }: ImagePrintPageProps) {
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [preview, setPreview] = useState<PreviewState>({});
  const [previewLoading, setPreviewLoading] = useState(false);
  const [previewError, setPreviewError] = useState<string | null>(null);
  const [printStatus, setPrintStatus] = useState<PrintStatus>({ kind: "idle" });

  // Default to "everything selected" whenever a different study is loaded,
  // and clear any leftover preview/print state from a previous study.
  useEffect(() => {
    setSelected(new Set(study?.images.map((image) => image.sopInstanceUid) ?? []));
    setPreview({});
    setPreviewError(null);
    setPrintStatus({ kind: "idle" });
  }, [study?.studyInstanceUid]);

  // Raw on-disk paths for the selected images, in the study's original
  // order (not Set insertion order) — this is what the host's
  // ImageSheetComposer needs.
  const selectedFilePaths = useMemo(() => {
    if (!study) {
      return [];
    }
    return study.images.filter((image) => selected.has(image.sopInstanceUid)).map((image) => image.filePath);
  }, [study, selected]);

  // Debounced recompose whenever the selection changes, so ticking several
  // thumbnails quickly doesn't fire a compose per click.
  useEffect(() => {
    if (selectedFilePaths.length === 0) {
      setPreview({});
      setPreviewError(null);
      setPreviewLoading(false);
      return;
    }

    let cancelled = false;
    setPreviewLoading(true);
    setPreviewError(null);

    const timer = setTimeout(async () => {
      const result = await composeImagePrintPreview(selectedFilePaths);
      if (cancelled) {
        return;
      }

      if (result.success) {
        setPreview(result);
        setPreviewError(null);
      } else {
        setPreview({});
        setPreviewError(result.error ?? "Failed to build preview.");
      }
      setPreviewLoading(false);
    }, 250);

    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
    // selectedFilePaths is a derived array (new identity each render), so
    // depend on its serialized form to avoid recomposing every render.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [selectedFilePaths.join("|")]);

  function toggleImage(sopInstanceUid: string) {
    setSelected((prev) => {
      const next = new Set(prev);
      if (next.has(sopInstanceUid)) {
        next.delete(sopInstanceUid);
      } else {
        next.add(sopInstanceUid);
      }
      return next;
    });
    setPrintStatus({ kind: "idle" });
  }

  function selectAll() {
    setSelected(new Set(study?.images.map((image) => image.sopInstanceUid) ?? []));
  }

  function selectNone() {
    setSelected(new Set());
  }

  async function handlePrint() {
    if (selectedFilePaths.length === 0) {
      return;
    }
    setPrintStatus({ kind: "printing" });
    const result = await printImageSheet(selectedFilePaths);
    setPrintStatus({
      kind: "result",
      success: result.success,
      message: result.message ?? result.error ?? (result.success ? "Print job sent." : "Print failed.")
    });
  }

  if (!study) {
    return (
      <div className="page">
        <h1>Image Print</h1>
        <StateMessage
          icon="🖨️"
          title="No study loaded"
          detail="Select a patient's study from Patient List and choose &quot;Print Images&quot; to load it here."
        />
      </div>
    );
  }

  return (
    <div className="page">
      <h1>Image Print</h1>
      <p className="image-print__subject">
        {study.patientName} ({study.patientId}) — {study.examType}
      </p>

      <div className="image-print__toolbar">
        <span>
          {selected.size} of {study.images.length} selected
        </span>
        <button type="button" onClick={selectAll} disabled={selected.size === study.images.length}>
          Select all
        </button>
        <button type="button" onClick={selectNone} disabled={selected.size === 0}>
          Select none
        </button>
      </div>

      <ThumbnailGrid images={study.images} selected={selected} onToggle={toggleImage} />

      <PrintPreview
        previewUrl={preview.previewUrl}
        pageCount={preview.pageCount}
        imagesComposed={preview.imagesComposed}
        skippedCount={preview.skippedCount}
        columns={preview.columns}
        rows={preview.rows}
        loading={previewLoading}
        error={previewError}
      />

      <div className="image-print__actions">
        <button
          type="button"
          className="image-print__print-button"
          onClick={handlePrint}
          disabled={selectedFilePaths.length === 0 || printStatus.kind === "printing"}
        >
          {printStatus.kind === "printing" ? <Spinner label="Printing..." /> : "Print"}
        </button>

        {printStatus.kind === "result" && (
          <p
            className={`image-print__print-status image-print__print-status--${
              printStatus.success ? "success" : "error"
            }`}
          >
            {printStatus.success ? "✓" : "⚠️"} {printStatus.message}
          </p>
        )}
      </div>
    </div>
  );
}
