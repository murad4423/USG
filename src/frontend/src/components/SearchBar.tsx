interface SearchBarProps {
  value: string;
  onChange: (value: string) => void;
}

/**
 * Fully controlled — every keystroke is reported immediately via
 * `onChange` so typing never feels laggy. Debouncing the resulting IPC
 * call (if any) is the caller's job, not this component's.
 */
export default function SearchBar({ value, onChange }: SearchBarProps) {
  return (
    <div className="search-bar">
      <input
        type="text"
        value={value}
        onChange={(e) => onChange(e.target.value)}
        placeholder="Search by patient name or ID..."
        aria-label="Search patients by name or ID"
      />
      {value && (
        <button
          type="button"
          className="search-bar__clear"
          onClick={() => onChange("")}
          aria-label="Clear search"
        >
          ×
        </button>
      )}
    </div>
  );
}
