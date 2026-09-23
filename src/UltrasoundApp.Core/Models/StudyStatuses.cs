namespace UltrasoundApp.Core.Models;

/// <summary>
/// The workflow values stored in <see cref="Study.Status"/>. A study moves
/// forward through them: it arrives as <see cref="Received"/> (shown as
/// "Waiting Study" in the Patient List), becomes <see cref="ImagePrinted"/>
/// once its images have been printed, and later <see cref="ReportPrinted"/>
/// and <see cref="Completed"/>. The same strings are used by the frontend
/// (frontend/src/studyStatus.ts) — keep them identical.
/// </summary>
public static class StudyStatuses
{
    public const string Received = "Received";
    public const string ImagePrinted = "ImagePrinted";
    public const string ReportPrinted = "ReportPrinted";
    public const string Completed = "Completed";

    /// <summary>True for <see cref="ImagePrinted"/> and every later step.</summary>
    public static bool IsImagePrintedOrLater(string? status)
    {
        return Is(status, ImagePrinted) || Is(status, ReportPrinted) || Is(status, Completed);
    }

    private static bool Is(string? status, string expected)
    {
        return string.Equals(status?.Trim(), expected, StringComparison.OrdinalIgnoreCase);
    }
}
