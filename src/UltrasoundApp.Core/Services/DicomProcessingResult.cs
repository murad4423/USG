using UltrasoundApp.Core.Models;

namespace UltrasoundApp.Core.Services;

/// <summary>
/// Outcome of running one parsed DICOM file through
/// <see cref="DicomProcessingService"/>. Lets callers (e.g. the upload UI)
/// report exactly what happened — including the idempotent "nothing new
/// needed to be created" case — without needing to re-query the database.
/// </summary>
public sealed class DicomProcessingResult
{
    /// <summary>The patient the image belongs to (pre-existing or newly created).</summary>
    public required Patient Patient { get; init; }

    /// <summary>The study the image belongs to (pre-existing or newly created).</summary>
    public required Study Study { get; init; }

    /// <summary>The image row that was ensured to exist.</summary>
    public required Image Image { get; init; }

    /// <summary>True if a new Patient row was inserted; false if one already existed.</summary>
    public required bool PatientWasCreated { get; init; }

    /// <summary>True if a new Study row was inserted; false if one already existed.</summary>
    public required bool StudyWasCreated { get; init; }

    /// <summary>
    /// True if a new Image row was inserted; false if this exact
    /// SOP Instance UID was already recorded (e.g. the same file, or the
    /// same study export, was uploaded again).
    /// </summary>
    public required bool ImageWasCreated { get; init; }
}
