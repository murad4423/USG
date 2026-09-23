using System.Collections.Generic;

namespace UltrasoundApp.Core.Models;

/// <summary>
/// Represents a report template associated with a specific exam type.
/// Plain data model only — no behavior, no persistence, no templating logic.
/// </summary>
public class ReportTemplate
{
    /// <summary>The exam type this template applies to (e.g. "Abdomen", "OB/GYN").</summary>
    public string ExamType { get; set; } = string.Empty;

    /// <summary>Full file path to the HTML template file on disk.</summary>
    public string HtmlTemplateFilePath { get; set; } = string.Empty;

    /// <summary>Names of the placeholder fields that appear in the HTML template (e.g. "PatientName", "Findings").</summary>
    public List<string> PlaceholderFields { get; set; } = new();
}
