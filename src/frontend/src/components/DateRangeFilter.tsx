export interface DateRangeValue {
  startDate: string; // "yyyy-MM-dd", or "" for open-ended
  endDate: string; // "yyyy-MM-dd", or "" for open-ended
}

interface DateRangeFilterProps {
  value: DateRangeValue;
  onChange: (value: DateRangeValue) => void;
}

/**
 * Two native date inputs narrowing the patient list to a day or range.
 * Either side can be left blank for an open-ended range. `<input
 * type="date">` only ever produces a valid "yyyy-MM-dd" string (or ""),
 * so there's no free-text date parsing to worry about here.
 */
export default function DateRangeFilter({ value, onChange }: DateRangeFilterProps) {
  const { startDate, endDate } = value;
  const hasRange = Boolean(startDate || endDate);
  const isInverted = Boolean(startDate && endDate && startDate > endDate);

  return (
    <div className="date-range-filter">
      <label className="date-range-filter__field">
        <span>From</span>
        <input
          type="date"
          value={startDate}
          max={endDate || undefined}
          onChange={(e) => onChange({ startDate: e.target.value, endDate })}
        />
      </label>

      <label className="date-range-filter__field">
        <span>To</span>
        <input
          type="date"
          value={endDate}
          min={startDate || undefined}
          onChange={(e) => onChange({ startDate, endDate: e.target.value })}
        />
      </label>

      {/*
        Always rendered (disabled while there's nothing to clear) so the
        toolbar keeps the same layout and stays on a single line.
      */}
      <button
        type="button"
        className="date-range-filter__clear"
        onClick={() => onChange({ startDate: "", endDate: "" })}
        disabled={!hasRange}
      >
        Clear
      </button>

      {isInverted && (
        <span className="date-range-filter__warning">"From" is after "To" — no results will match.</span>
      )}
    </div>
  );
}
