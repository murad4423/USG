import { useEffect, useState } from "react";
import {
  getImageTemplate,
  saveImageTemplateContentArea,
  saveImageTemplatePlaceholders,
  uploadImageTemplate,
  type ContentArea,
  type TemplatePlaceholder
} from "../ipcImageTemplate";
import { Spinner } from "./StateMessage";
import ImageTemplateAreaEditor from "./ImageTemplateAreaEditor";

type Message = { kind: "success" | "error" | "neutral"; text: string };

/**
 * Settings section: upload a PDF to use as the "Image Template", and choose
 * the "image area" on it. The upload button opens a native file picker; the
 * chosen PDF is copied into the app's data/image-templates folder (one
 * template at a time — a new upload replaces the old one and clears the
 * old image area). The "Select Image Area" button opens
 * ImageTemplateAreaEditor, where the operator drags a rectangle over the
 * template: only that rectangle is used for the selected images in the
 * print preview and in the printed sheet, so the template's own
 * header/footer stay visible. The area is saved on the host and can be
 * changed again at any time. The same editor is also where the operator adds
 * patient-information placeholders (name, ID, age, sex...) on the template;
 * they are saved together with the image area. Both actions apply
 * immediately; they are independent of the page's Save button.
 */
export default function ImageTemplateUpload() {
  const [currentFileName, setCurrentFileName] = useState<string | null>(null);
  const [backgroundUrl, setBackgroundUrl] = useState<string | undefined>(undefined);
  const [contentArea, setContentArea] = useState<ContentArea | null>(null);
  const [placeholders, setPlaceholders] = useState<TemplatePlaceholder[]>([]);
  const [uploading, setUploading] = useState(false);
  const [message, setMessage] = useState<Message | null>(null);

  const [editorOpen, setEditorOpen] = useState(false);
  const [savingArea, setSavingArea] = useState(false);
  const [areaError, setAreaError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    getImageTemplate().then((result) => {
      if (!cancelled && result.success) {
        setCurrentFileName(result.hasTemplate ? result.fileName ?? null : null);
        setBackgroundUrl(result.hasTemplate ? result.backgroundImageUrl : undefined);
        setContentArea(result.hasTemplate ? result.contentArea ?? null : null);
        setPlaceholders(result.hasTemplate ? result.placeholders ?? [] : []);
      }
    });
    return () => {
      cancelled = true;
    };
  }, []);

  async function handleUploadClick() {
    setUploading(true);
    setMessage(null);
    const result = await uploadImageTemplate();
    setUploading(false);

    if (result.success) {
      setCurrentFileName(result.fileName ?? null);
      setBackgroundUrl(result.backgroundImageUrl);
      // The host clears the old image area when a new template is uploaded.
      setContentArea(result.contentArea ?? null);
      // Saved placeholders stay with the app (only the image area is reset by an upload) — reload them.
      const refreshed = await getImageTemplate();
      if (refreshed.success) {
        setPlaceholders(refreshed.placeholders ?? []);
      }
      setMessage({
        kind: "success",
        text: `Uploaded "${result.fileName}". Now click "Select Image Area" to choose where the images go.`
      });
    } else if (result.cancelled) {
      setMessage({ kind: "neutral", text: "No file selected." });
    } else {
      setMessage({ kind: "error", text: result.error ?? "Could not upload the template." });
    }
  }

  function openEditor() {
    setAreaError(null);
    setEditorOpen(true);
  }

  async function handleSaveArea(area: ContentArea | null, newPlaceholders: TemplatePlaceholder[]) {
    setSavingArea(true);
    setAreaError(null);

    const areaResult = await saveImageTemplateContentArea(area);
    if (!areaResult.success) {
      setSavingArea(false);
      setAreaError(areaResult.error ?? "Could not save the image area.");
      return;
    }
    setContentArea(areaResult.contentArea ?? null);

    const placeholderResult = await saveImageTemplatePlaceholders(newPlaceholders);
    setSavingArea(false);
    if (!placeholderResult.success) {
      setAreaError(placeholderResult.error ?? "Could not save the patient information boxes.");
      return;
    }
    const saved = placeholderResult.placeholders ?? [];
    setPlaceholders(saved);
    setEditorOpen(false);

    const areaText = areaResult.contentArea
      ? "Image area saved."
      : "Image area cleared — images use the normal page margins.";
    const placeholderText =
      saved.length > 0
        ? ` ${saved.length} patient information box${saved.length === 1 ? "" : "es"} saved.`
        : " No patient information boxes.";
    setMessage({ kind: "success", text: areaText + placeholderText });
  }

  const canSelectArea = Boolean(currentFileName && backgroundUrl);

  return (
    <section className="settings__section">
      <h2 className="settings__section-title">Image Template</h2>
      <div style={{ display: "flex", flexWrap: "wrap", gap: 14 }}>
        <button type="button" className="settings__tool-button" onClick={handleUploadClick} disabled={uploading}>
          {uploading ? <Spinner label="Uploading..." /> : "Upload Image Template (PDF)"}
        </button>
        <button
          type="button"
          className="settings__tool-button"
          onClick={openEditor}
          disabled={!canSelectArea || uploading}
          title={canSelectArea ? "Choose where the images are printed on the template" : "Upload a template first"}
        >
          Select Image Area
        </button>
      </div>

      <p className="settings__hint">
        {currentFileName ? `Current template: ${currentFileName}` : "No image template uploaded yet."} Uploading a new
        PDF replaces the current one (and its image area). This takes effect immediately — no need to press Save.
      </p>
      {currentFileName && (
        <p className="settings__hint">
          {contentArea
            ? "Image area: selected. Only this part of the template is used for the images; click \"Select Image Area\" to change it."
            : "Image area: not selected yet — images use the normal page margins and may cover the template's header/footer."}
        </p>
      )}

      {currentFileName && (
        <p className="settings__hint">
          {placeholders.length > 0
            ? `Patient information boxes: ${placeholders.length} on the template (name, ID, age... are filled in for each patient when images are previewed/printed).`
            : "Patient information boxes: none yet — open \"Select Image Area\" and use the \"Patient information\" dropdown to add them."}
        </p>
      )}

      {message && (
        <p
          className={`settings__message ${
            message.kind === "error" ? "settings__message--error" : "settings__message--success"
          }`}
          style={message.kind === "neutral" ? { color: "#868e96" } : undefined}
        >
          {message.kind === "error" ? "⚠️" : message.kind === "success" ? "✓" : ""} {message.text}
        </p>
      )}

      {editorOpen && backgroundUrl && (
        <ImageTemplateAreaEditor
          backgroundImageUrl={backgroundUrl}
          initialArea={contentArea}
          initialPlaceholders={placeholders}
          saving={savingArea}
          error={areaError}
          onSave={handleSaveArea}
          onCancel={() => setEditorOpen(false)}
        />
      )}
    </section>
  );
}
