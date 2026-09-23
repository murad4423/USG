import { useState } from "react";
import { uploadDicomFile, type UploadDicomResult } from "../ipc";
import { Spinner } from "./StateMessage";

type Status =
  | { kind: "idle" }
  | { kind: "uploading" }
  | { kind: "result"; result: UploadDicomResult };

function describeSuccess(result: UploadDicomResult): string {
  const parts: string[] = [];

  parts.push(
    result.patientWasCreated
      ? `New patient "${result.patientName}" added.`
      : `Matched existing patient "${result.patientName}".`
  );

  parts.push(
    result.studyWasCreated
      ? `New study created (${result.examType || "unknown exam type"}).`
      : `Added to existing study (${result.examType || "unknown exam type"}).`
  );

  parts.push(
    result.imageWasCreated
      ? "Image imported."
      : "This image was already imported — no duplicate created."
  );

  return parts.join(" ");
}

interface DicomUploadControlProps {
  /** Called after every upload attempt (including cancellations/failures) so a parent can e.g. refresh a list on success. */
  onUploadComplete?: (result: UploadDicomResult) => void;
}

export default function DicomUploadControl({ onUploadComplete }: DicomUploadControlProps = {}) {
  const [status, setStatus] = useState<Status>({ kind: "idle" });

  async function handleUploadClick() {
    setStatus({ kind: "uploading" });
    const result = await uploadDicomFile();
    setStatus({ kind: "result", result });
    onUploadComplete?.(result);
  }

  const isUploading = status.kind === "uploading";

  return (
    <div className="dicom-upload">
      <button type="button" onClick={handleUploadClick} disabled={isUploading}>
        {isUploading ? <Spinner label="Processing..." /> : "Manual DICOM Upload"}
      </button>

      {status.kind === "result" && status.result.cancelled && (
        <p className="dicom-upload__message dicom-upload__message--neutral">
          No file selected.
        </p>
      )}

      {status.kind === "result" && !status.result.cancelled && !status.result.success && (
        <p className="dicom-upload__message dicom-upload__message--error">
          ⚠️ Upload failed{status.result.fileName ? ` (${status.result.fileName})` : ""}:{" "}
          {status.result.error ?? "Unknown error."}
        </p>
      )}

      {status.kind === "result" && status.result.success && (
        <p className="dicom-upload__message dicom-upload__message--success">
          ✓ {status.result.fileName ? `${status.result.fileName}: ` : ""}
          {describeSuccess(status.result)}
        </p>
      )}
    </div>
  );
}
