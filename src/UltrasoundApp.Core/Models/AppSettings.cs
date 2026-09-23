namespace UltrasoundApp.Core.Models;

/// <summary>
/// App-wide configuration: the DICOM auto-receive listener's identity,
/// configurable template folder locations, the operator's chosen
/// printer, and hospital branding fields for printed output. Plain data
/// model only — no persistence, no default-value logic, no validation
/// (see <see cref="Services.SettingsService"/> for defaulting).
///
/// Unlike every other model in this app (Patient, Study, ...), there is
/// exactly one of these — see <see cref="Repositories.ISettingsRepository"/>.
///
/// Per this step's scope, nothing yet reads these values back out to
/// actually configure anything: DicomScpListener/ImageSheetComposer/
/// SilentPrinter/report-template resolution all still use their own
/// hardcoded defaults, unaware this class exists. Wiring that up is a
/// later step.
/// </summary>
public class AppSettings
{
    /// <summary>DICOM AE Title the auto-receive listener (DicomScpListener) should accept associations for.</summary>
    public string AeTitle { get; set; } = string.Empty;

    /// <summary>TCP port the auto-receive listener should bind to.</summary>
    public int ListeningPort { get; set; }

    /// <summary>
    /// Folder containing the report HTML templates. Null/empty means "use
    /// the built-in default" (currently <c>UltrasoundApp.Templating.TemplatingPaths.GetReportTemplatesDirectory()</c>).
    /// </summary>
    public string? ReportTemplatesFolderPath { get; set; }

    /// <summary>
    /// Path to the image-sheet branding template JSON file. Null/empty
    /// means "use the built-in default" (currently
    /// <c>UltrasoundApp.Printing.PrintingPaths.GetDefaultBrandingTemplatePath()</c>).
    /// </summary>
    public string? ImageSheetBrandingTemplatePath { get; set; }

    /// <summary>Windows printer name silent print jobs should be sent to. Null/empty means the system default printer.</summary>
    public string? PrinterName { get; set; }

    /// <summary>Path to the hospital's logo image file, for printed output branding.</summary>
    public string? LogoPath { get; set; }

    /// <summary>Hospital name, for printed output branding.</summary>
    public string HospitalName { get; set; } = string.Empty;

    /// <summary>Hospital address/contact info, for printed output branding.</summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>Free-text line for the printed page header (e.g. a report title).</summary>
    public string? HeaderText { get; set; }

    /// <summary>Free-text line for the printed page footer.</summary>
    public string? FooterText { get; set; }
}
