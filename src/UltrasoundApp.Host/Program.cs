using UltrasoundApp.Core.Logging;
using UltrasoundApp.Core.Models;
using UltrasoundApp.Core.Repositories;
using UltrasoundApp.Core.Services;
using UltrasoundApp.Data;
using UltrasoundApp.Data.Repositories;
using UltrasoundApp.Dicom;
using UltrasoundApp.Printing;
using UltrasoundApp.Templating;

namespace UltrasoundApp.Host;

internal static class Program
{
    /// <summary>
    /// Application entry point. Ensures the SQLite database (data/app.db)
    /// and its schema exist, wires up the data layer + DICOM processing
    /// pipeline, then launches the main shell window.
    /// </summary>
    [STAThread]
    private static void Main()
    {
        // Catch-all crash handlers, installed before anything else can
        // fail. These do not attempt recovery — the point is that an
        // otherwise-silent crash leaves a diagnosable trace in
        // data/logs/ rather than the app simply vanishing on a clinic
        // machine with no terminal attached.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            AppLog.Error("APP", "Unhandled exception — the app is terminating.", e.ExceptionObject as Exception);
        Application.ThreadException += (_, e) =>
            AppLog.Error("APP", "Unhandled exception on the UI thread.", e.Exception);

        AppLog.Info("APP", $"Application starting. Logging to '{AppLog.GetLogDirectory()}'.");

        var dbContext = new AppDbContext();
        DbInitializer.Initialize(dbContext);

        var patientRepository = new PatientRepository(dbContext);
        var studyRepository = new StudyRepository(dbContext);
        var imageRepository = new ImageRepository(dbContext);
        var reportTemplateRepository = new ReportTemplateRepository(dbContext);
        var generatedReportRepository = new GeneratedReportRepository(dbContext);
        var printJobRepository = new PrintJobRepository(dbContext);
        var settingsRepository = new SettingsRepository(dbContext);
        var settingsService = new SettingsService(settingsRepository);

        var dicomFileParser = new DicomFileParser();
        var dicomProcessingService = new DicomProcessingService(
            patientRepository, studyRepository, imageRepository);
        var patientStudyService = new PatientStudyService(
            patientRepository, studyRepository, imageRepository);

        // Background auto-receive: the exact same dicomFileParser/
        // dicomProcessingService instances constructed above, so a study
        // pushed to this listener is processed identically to one dragged
        // in through Manual DICOM Upload — see DicomScpListener's class
        // doc-comment. AE Title/port now come from Settings (falling back
        // to SettingsService's own built-in defaults, which match
        // DicomScpListener's old hardcoded ones, on a fresh database) —
        // WebViewBridge's "saveSettings" handler calls
        // dicomScpListener.Restart(...) if the operator changes either
        // value while the app is running, so this initial Start() is only
        // what applies AT LAUNCH. Start() never throws — a bind failure
        // (e.g. port already in use) is caught inside the class itself,
        // logged via its default Console logging, and reported back as a
        // `false` return here; the rest of the app (including manual
        // upload, which doesn't depend on this listener at all) must keep
        // working either way, so a failed Start() is deliberately NOT
        // treated as fatal — nothing here throws, exits, or blocks the
        // window below from opening.
        AppSettings initialSettings = settingsService.Get();
        var dicomScpListener = new DicomScpListener(
            dicomFileParser, dicomProcessingService, initialSettings.AeTitle, initialSettings.ListeningPort);
        dicomScpListener.Start();

        var imageSheetComposer = new ImageSheetComposer();
        var imageSheetBranding = ImageSheetBrandingTemplateLoader.LoadDefault();
        var silentPrinter = new SilentPrinter();

        SeedDefaultReportTemplates(reportTemplateRepository);

        // TemplateEngine/PdfRenderer implement Core's IReportTemplateEngine /
        // IReportPdfRenderer (explicit interface implementation), so they
        // plug straight into ReportRenderService with no adapter needed —
        // see UltrasoundApp.ReportRender.ManualTest for the same wiring.
        // PdfRenderer starts a hidden headless WebView2 host on its own STA
        // thread; this can take a couple of seconds on first launch.
        var templateEngine = new TemplateEngine();
        var pdfRenderer = new PdfRenderer();
        var reportRenderService = new ReportRenderService(
            patientRepository,
            studyRepository,
            reportTemplateRepository,
            generatedReportRepository,
            templateEngine,
            pdfRenderer);

        ApplicationConfiguration.Initialize();
        Application.ApplicationExit += (_, _) =>
        {
            AppLog.Info("APP", "Application exiting.");
            pdfRenderer.Dispose();
            dicomScpListener.Dispose();
        };

        Application.Run(new MainForm(
            dicomFileParser,
            dicomProcessingService,
            patientStudyService,
            imageSheetComposer,
            imageSheetBranding,
            silentPrinter,
            reportRenderService,
            templateEngine,
            reportTemplateRepository,
            generatedReportRepository,
            printJobRepository,
            settingsService,
            dicomScpListener));
    }

    /// <summary>
    /// Ensures the two example report templates from the templating step
    /// (templates/reports/*.html) have a matching ReportTemplate row, so a
    /// freshly-created app.db can generate reports for those exam types
    /// out of the box. Idempotent — safe to run on every launch.
    ///
    /// A real deployment would configure these (and map them to whatever
    /// exam-type strings its DICOM modality actually sends in
    /// StudyDescription) via a settings UI; that configuration screen is
    /// out of scope for this step, so it's seeded here instead. If a test
    /// study's ExamType doesn't match either seeded key, add a matching
    /// ReportTemplate row by hand (see docs/README.md) rather than
    /// changing this list.
    /// </summary>
    private static void SeedDefaultReportTemplates(IReportTemplateRepository reportTemplateRepository)
    {
        SeedReportTemplateIfMissing(reportTemplateRepository, "WholeAbdomen", "whole-abdomen.html");
        SeedReportTemplateIfMissing(reportTemplateRepository, "PregnancyProfile", "pregnancy-profile.html");
    }

    private static void SeedReportTemplateIfMissing(
        IReportTemplateRepository reportTemplateRepository, string examType, string templateFileName)
    {
        if (reportTemplateRepository.GetByExamType(examType) is not null)
        {
            return;
        }

        reportTemplateRepository.Add(new ReportTemplate
        {
            ExamType = examType,
            HtmlTemplateFilePath = TemplatingPaths.GetReportTemplatePath(templateFileName),
            PlaceholderFields = new List<string>(), // not consulted by ReportRenderService — TemplateEngine scans the template itself
        });
    }
}
