namespace UltrasoundApp.Dicom;

/// <summary>
/// One DICOM tag as discovered by <see cref="DicomTagInspector"/>: its
/// identity, VR (value type), current value (may be empty even though the
/// tag itself is present in the file), and which UI section it should be
/// grouped under.
/// </summary>
public sealed class DicomFieldInfo
{
    /// <summary>Standard "(gggg,eeee)" tag notation, e.g. "(0010,0010)".</summary>
    public required string Tag { get; init; }

    /// <summary>Machine-friendly name, e.g. "PatientName" — the token to use when wiring this field into a report template.</summary>
    public required string Keyword { get; init; }

    /// <summary>Human-readable name, e.g. "Patient's Name".</summary>
    public required string Name { get; init; }

    /// <summary>Value Representation code, e.g. "PN", "DA", "SQ".</summary>
    public required string Vr { get; init; }

    /// <summary>
    /// The tag's value as text. Empty string means the tag is present in
    /// the file but has no value entered (e.g. Patient Sex present but
    /// blank for this particular patient) — this is deliberately kept
    /// distinct from the tag not existing at all, which simply never
    /// appears in the returned list.
    /// </summary>
    public required string Value { get; init; }

    /// <summary>Coarse grouping for display: "Patient", "Study / Exam", "Measurements", or "Other".</summary>
    public required string Category { get; init; }

    /// <summary>Empty for a top-level tag; otherwise describes which sequence/item this tag is nested under (e.g. "Content Sequence[0]").</summary>
    public required string Path { get; init; }
}

/// <summary>Everything <see cref="DicomTagInspector.Inspect"/> found in one DICOM file.</summary>
public sealed class DicomInspectionResult
{
    public required string FileName { get; init; }
    public required List<DicomFieldInfo> Fields { get; init; }
}
