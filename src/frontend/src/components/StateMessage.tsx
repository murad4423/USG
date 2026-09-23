interface StateMessageProps {
  /** Short heading, e.g. "No patients yet" or "Couldn't load patients". */
  title: string;
  /** Optional longer explanation shown under the title. */
  detail?: string;
  /** A small illustrative glyph — kept to a single emoji/character, never an image asset. */
  icon?: string;
  /** Optional action, e.g. a "Retry" or "Upload a DICOM file" button. */
  action?: { label: string; onClick: () => void; disabled?: boolean };
  variant?: "empty" | "error";
}

/**
 * Shared "nothing to show" / "something went wrong" block used by every
 * page and list in the app (Patient List, Image Print, Report Print,
 * Settings) so empty and error states look and behave the same way
 * everywhere instead of each page inventing its own one-line message.
 */
export default function StateMessage({ title, detail, icon, action, variant = "empty" }: StateMessageProps) {
  return (
    <div className={`state-message state-message--${variant}`} role={variant === "error" ? "alert" : undefined}>
      {icon && (
        <span className="state-message__icon" aria-hidden="true">
          {icon}
        </span>
      )}
      <p className="state-message__title">{title}</p>
      {detail && <p className="state-message__detail">{detail}</p>}
      {action && (
        <button type="button" className="state-message__action" onClick={action.onClick} disabled={action.disabled}>
          {action.label}
        </button>
      )}
    </div>
  );
}

/** Small inline spinner + label used for in-progress states (loading a list, composing a preview, printing). */
export function Spinner({ label }: { label: string }) {
  return (
    <span className="spinner">
      <span className="spinner__ring" aria-hidden="true" />
      {label}
    </span>
  );
}

/** Full-block loading state (a whole page/section is waiting on its first load), as opposed to the inline Spinner above. */
export function LoadingBlock({ label }: { label: string }) {
  return (
    <div className="loading-block">
      <span className="spinner__ring spinner__ring--lg" aria-hidden="true" />
      <p className="loading-block__label">{label}</p>
    </div>
  );
}
