using System;

namespace UltrasoundApp.Core.Models;

/// <summary>
/// Represents a patient identified by their DICOM patient ID.
/// Plain data model only — no behavior, no persistence.
/// </summary>
public class Patient
{
    /// <summary>DICOM Patient ID (DICOM tag 0010,0020).</summary>
    public string PatientID { get; set; } = string.Empty;

    /// <summary>Patient's full name.</summary>
    public string PatientName { get; set; } = string.Empty;

    /// <summary>Patient's date of birth, if known.</summary>
    public DateTime? DateOfBirth { get; set; }

    /// <summary>Patient's age at time of study, if DOB is not available.</summary>
    public int? Age { get; set; }

    /// <summary>Patient's sex (e.g. "M", "F", "O").</summary>
    public string? Sex { get; set; }
}
