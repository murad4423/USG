using System;

namespace UltrasoundApp.Core.Models;

/// <summary>
/// Represents a report that has been generated (filled in and rendered) for a study.
/// Plain data model only — no behavior, no persistence, no PDF generation logic.
/// </summary>
public class GeneratedReport
{
    /// <summary>Foreign key to the <see cref="Study"/> this report was generated for.</summary>
    public string StudyInstanceUID { get; set; } = string.Empty;

    /// <summary>Filled-in template field values, serialized as a JSON string (field name -> value).</summary>
    public string FieldValuesJson { get; set; } = string.Empty;

    /// <summary>Full file path to the generated PDF report on disk.</summary>
    public string? GeneratedPdfPath { get; set; }

    /// <summary>Timestamp when this report was first created.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Timestamp when this report was last modified.</summary>
    public DateTime ModifiedAt { get; set; }
}
