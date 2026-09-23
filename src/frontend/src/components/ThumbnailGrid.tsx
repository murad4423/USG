import { useState } from "react";
import type { PatientStudyImage } from "../ipc";
import StateMessage from "./StateMessage";

interface ThumbnailGridProps {
  images: PatientStudyImage[];
  selected: Set<string>;
  onToggle: (sopInstanceUid: string) => void;
}

/**
 * Grid of a study's images, each toggleable on/off, used by
 * PatientDetailPage to build the subset that gets composed into a print
 * sheet. Purely a
 * selection control — the actual sheet layout/pagination is computed
 * server-side by ImageSheetComposer and shown in PrintPreview.
 */
export default function ThumbnailGrid({ images, selected, onToggle }: ThumbnailGridProps) {
  const [brokenThumbnails, setBrokenThumbnails] = useState<Set<string>>(new Set());

  if (images.length === 0) {
    return (
      <StateMessage
        icon="🖼️"
        title="No images in this study"
        detail="This study was recorded without any image instances, so there's nothing to select for an image sheet."
      />
    );
  }

  return (
    <div className="thumbnail-grid">
      {images.map((image) => {
        const isSelected = selected.has(image.sopInstanceUid);
        const showFallback = !image.thumbnailUrl || brokenThumbnails.has(image.sopInstanceUid);

        return (
          <button
            type="button"
            key={image.sopInstanceUid}
            className={`thumbnail-grid__item${isSelected ? " thumbnail-grid__item--selected" : ""}`}
            title={image.sopInstanceUid}
            aria-pressed={isSelected}
            onClick={() => onToggle(image.sopInstanceUid)}
          >
            <span className="thumbnail-grid__checkbox" aria-hidden="true">
              {isSelected ? "✓" : ""}
            </span>

            {showFallback ? (
              <span className="thumbnail-grid__fallback">No preview</span>
            ) : (
              <img
                src={image.thumbnailUrl}
                alt=""
                loading="lazy"
                onError={() => setBrokenThumbnails((prev) => new Set(prev).add(image.sopInstanceUid))}
              />
            )}
          </button>
        );
      })}
    </div>
  );
}
