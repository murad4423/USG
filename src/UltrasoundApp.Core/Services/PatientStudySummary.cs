using UltrasoundApp.Core.Models;

namespace UltrasoundApp.Core.Services;

/// <summary>
/// One row of a patient/study listing: a single <see cref="Study"/>,
/// the <see cref="Patient"/> it belongs to, and every <see cref="Image"/>
/// grouped underneath it. This is what <see cref="PatientStudyService"/>
/// returns — one instance per Study, never per Patient or per Image.
/// </summary>
public sealed class PatientStudySummary
{
    /// <summary>The patient who owns <see cref="Study"/>.</summary>
    public required Patient Patient { get; init; }

    /// <summary>The study this row represents.</summary>
    public required Study Study { get; init; }

    /// <summary>Every image recorded under this study, in no particular order.</summary>
    public required IReadOnlyList<Image> Images { get; init; }
}
