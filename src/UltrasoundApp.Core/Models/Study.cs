using System;

namespace UltrasoundApp.Core.Models;

/// <summary>
/// Represents a single imaging study (exam) performed on a patient.
/// Plain data model only — no behavior, no persistence.
/// </summary>
public class Study
{
    /// <summary>DICOM Study Instance UID (DICOM tag 0020,000D). Primary identifier for the study.</summary>
    public string StudyInstanceUID { get; set; } = string.Empty;

    /// <summary>Foreign key to the owning <see cref="Patient"/>.</summary>
    public string PatientID { get; set; } = string.Empty;

    /// <summary>Type of exam performed (e.g. "Abdomen", "OB/GYN", "Vascular").</summary>
    public string ExamType { get; set; } = string.Empty;

    /// <summary>Date and time the study was performed.</summary>
    public DateTime StudyDateTime { get; set; }

    /// <summary>Current workflow status of the study (e.g. "Scheduled", "InProgress", "Completed").</summary>
    public string Status { get; set; } = string.Empty;
}
