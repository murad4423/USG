using System;

namespace UltrasoundApp.Core.Models;

/// <summary>
/// Type of content a <see cref="PrintJob"/> represents.
/// </summary>
public enum PrintJobType
{
    ImageSheet,
    Report
}

/// <summary>
/// Represents a record of a print operation (image sheet or report) for a study.
/// Plain data model only — no behavior, no persistence, no printing logic.
/// </summary>
public class PrintJob
{
    /// <summary>Type of content that was printed.</summary>
    public PrintJobType Type { get; set; }

    /// <summary>Foreign key to the <see cref="Study"/> this print job relates to.</summary>
    public string StudyInstanceUID { get; set; } = string.Empty;

    /// <summary>Timestamp when the print job was executed.</summary>
    public DateTime Timestamp { get; set; }

    /// <summary>Name of the printer used for this job.</summary>
    public string PrinterUsed { get; set; } = string.Empty;
}
