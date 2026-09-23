namespace UltrasoundApp.Core.Models;

/// <summary>
/// Represents a single DICOM image instance belonging to a study.
/// Plain data model only — no behavior, no persistence.
/// </summary>
public class Image
{
    /// <summary>DICOM SOP Instance UID (DICOM tag 0008,0018). Primary identifier for the image.</summary>
    public string SOPInstanceUID { get; set; } = string.Empty;

    /// <summary>Foreign key to the owning <see cref="Study"/>.</summary>
    public string StudyInstanceUID { get; set; } = string.Empty;

    /// <summary>Full file path to the extracted/stored image file on disk.</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>Full file path to the generated thumbnail for this image.</summary>
    public string? ThumbnailPath { get; set; }
}
