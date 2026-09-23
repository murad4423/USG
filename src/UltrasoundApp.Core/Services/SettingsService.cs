using UltrasoundApp.Core.Models;
using UltrasoundApp.Core.Repositories;

namespace UltrasoundApp.Core.Services;

/// <summary>
/// Thin orchestration over <see cref="ISettingsRepository"/>: guarantees
/// callers always get a usable <see cref="AppSettings"/> instance (never
/// null) by applying reasonable built-in defaults the first time the app
/// runs, before anything has ever been explicitly saved.
///
/// This is deliberately just save/load, per this step's scope — nothing
/// here (or anywhere else yet) reads these values back out to actually
/// configure DicomScpListener/ImageSheetComposer/SilentPrinter/report-
/// template resolution; that wiring is a later step. Likewise there is no
/// SettingsPage.tsx or IPC handler yet — this class is only reachable
/// from C# for now (see <c>UltrasoundApp.Settings.ManualTest</c> for how
/// to exercise it without a UI).
/// </summary>
public sealed class SettingsService
{
    /// <summary>
    /// Same literal value as <c>UltrasoundApp.Dicom.DicomScpListener.DefaultAeTitle</c>.
    /// Duplicated rather than referenced: Core (this project) intentionally
    /// has zero project references (see UltrasoundApp.Core.csproj), so it
    /// can't read that constant directly, and this class deliberately
    /// isn't wired to DicomScpListener yet anyway (next step). Keep the
    /// two in sync by hand until that step removes the duplication —
    /// likely by having DicomScpListener's defaults come from
    /// SettingsService instead of the other way around.
    /// </summary>
    private const string DefaultAeTitle = "ULTRASOUNDAPP";

    /// <summary>Same literal value as <c>UltrasoundApp.Dicom.DicomScpListener.DefaultPort</c> — see <see cref="DefaultAeTitle"/>'s comment.</summary>
    private const int DefaultPort = 11112;

    private readonly ISettingsRepository _settingsRepository;

    public SettingsService(ISettingsRepository settingsRepository)
    {
        _settingsRepository = settingsRepository;
    }

    /// <summary>
    /// The current settings, or a fresh <see cref="AppSettings"/> with
    /// built-in defaults applied if nothing has ever been saved. That
    /// default instance is NOT persisted just by calling this — it's
    /// handed back so a caller (e.g. a future Settings UI) has sensible
    /// values to show/start from; call <see cref="Save"/> explicitly if
    /// it should stick.
    /// </summary>
    public AppSettings Get()
    {
        return _settingsRepository.Get() ?? CreateDefault();
    }

    /// <summary>
    /// Persists <paramref name="settings"/> as the app's current settings,
    /// replacing whatever (if anything) was saved before. A subsequent
    /// <see cref="Get"/> — including from a new <see cref="SettingsService"/>
    /// instance after an app restart — returns exactly these values back.
    /// </summary>
    public void Save(AppSettings settings)
    {
        _settingsRepository.Save(settings);
    }

    /// <summary>
    /// Built-in defaults for a fresh install: AE Title/port matching
    /// DicomScpListener's own hardcoded defaults (see
    /// <see cref="DefaultAeTitle"/>'s comment), folder-path overrides left
    /// null (meaning "use the built-in default" once a later step wires
    /// that up), and placeholder branding text in the same spirit as
    /// <c>UltrasoundApp.Printing.ImageSheetBrandingTemplate.CreateBuiltInDefault</c>.
    /// </summary>
    private static AppSettings CreateDefault() => new()
    {
        AeTitle = DefaultAeTitle,
        ListeningPort = DefaultPort,
        ReportTemplatesFolderPath = null,
        ImageSheetBrandingTemplatePath = null,
        PrinterName = null,
        LogoPath = null,
        HospitalName = "General Hospital",
        Address = "123 Main Street, Anytown, ST 00000",
        HeaderText = "Ultrasound Image Report",
        FooterText = "Confidential — For Clinical Use Only",
    };
}
