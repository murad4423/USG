using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Web.WebView2.WinForms;
using UltrasoundApp.Core.Logging;
using UltrasoundApp.Core.Models;
using UltrasoundApp.Core.Repositories;
using UltrasoundApp.Core.Services;
using UltrasoundApp.Dicom;
using UltrasoundApp.Printing;
using UltrasoundApp.Templating;

namespace UltrasoundApp.Host;

/// <summary>
/// Native &lt;-&gt; web IPC bridge.
///
/// Started as a "ping" -&gt; "pong" proof that the round-trip works, then
/// grew a real handler for manual DICOM upload. This step adds read-only
/// query handlers on top of <see cref="PatientStudyService"/>:
///
///   - "listPatientStudies"              -&gt; every study, grouped with its patient/images
///   - "searchPatientStudies"            -&gt; {"searchText": "..."} filtered by patient name/ID
///   - "filterPatientStudiesByDateRange" -&gt; {"startDate": "yyyy-MM-dd", "endDate": "yyyy-MM-dd"}
///
/// Each replies with a single "{requestType}Result" message containing
/// either {"success":true,"records":[...]} or {"success":false,"error":"..."}.
/// The Patient List UI (PatientListPage/PatientTable) consumes these.
///
/// Each image DTO also carries an "imageUrl"/"thumbnailUrl" alongside the
/// raw on-disk "filePath"/"thumbnailPath" — the former are rewritten to
/// "https://appimages/..." via MainForm's virtual-host mapping so an
/// &lt;img&gt; tag in React can actually load them (Chromium blocks file://
/// subresources from http/https pages).
///
/// This step adds the image-print handlers on top of
/// <see cref="ImageSheetComposer"/>/<see cref="SilentPrinter"/>:
///
///   - "composeImagePrintPreview" -&gt; {"imageFilePaths": ["...", ...]}
///     Composes those files (raw on-disk paths, as reported in an image
///     DTO's "filePath") into a PDF under data/print-preview and replies
///     with a "https://printpreview/..." URL PrintPreview.tsx can load
///     directly into an &lt;iframe&gt; (WebView2 renders PDFs natively).
///   - "printImageSheet" -&gt; {"imageFilePaths": ["...", ...], "printerName"?: "..."}
///     Composes the same way, then hands the PDF to SilentPrinter.
///
/// This step adds the report-print handlers on top of
/// <see cref="ReportRenderService"/> — the same service the manual test
/// harness (UltrasoundApp.ReportRender.ManualTest) exercises directly.
/// None of the actual auto-fill/merge/render/save logic is duplicated
/// here; this class only translates IPC requests into calls on it:
///
///   - "getReportFormFields" -&gt; {"examType": "..."}
///     Looks up the exam type's ReportTemplate row and introspects which
///     placeholder tokens its HTML actually contains (via the same
///     TemplateEngine.Merge scan ReportRenderService itself uses,
///     called with an empty field dictionary so every token comes back
///     as "missing"), minus the auto-filled ones — i.e. exactly the
///     manual fields ReportForm.tsx needs to render.
///   - "getSavedReportFieldValues" -&gt; {"studyInstanceUid": "..."}
///     Returns the study's existing GeneratedReport field values (if a
///     report was generated before), so re-opening a study resumes with
///     its previous content instead of a blank form.
///   - "composeReportPreview" -&gt; {"studyInstanceUid": "...", "manualFieldValues": {...}}
///     Calls ReportRenderService.GenerateReportAsync with a scratch
///     output path under data/print-preview and replies with a
///     "https://printpreview/..." URL, the same way
///     "composeImagePrintPreview" does for image sheets.
///   - "printReport" -&gt; {"studyInstanceUid": "...", "manualFieldValues": {...}, "printerName"?: "..."}
///     Calls ReportRenderService.GenerateReportAsync with a stable
///     per-study output path, then hands the resulting PDF to the same
///     SilentPrinter instance "printImageSheet" uses.
///
/// This step adds reprint-from-stored-record handlers on top of
/// <see cref="IPrintJobRepository"/> — reprinting never touches the
/// original DICOM file, only data already persisted from a prior
/// import/print:
///
///   - "reprintImages" -&gt; {"studyInstanceUid": "...", "printerName"?: "..."}
///     Looks up the study's already-extracted image file paths itself
///     (via PatientStudyService/ImageRepository — the caller only
///     supplies the study, not a fresh image selection), composes and
///     prints them exactly like "printImageSheet", then logs a new
///     PrintJob row (Type=ImageSheet).
///   - "reprintReport" -&gt; {"studyInstanceUid": "...", "manualFieldValues"?: {...}, "printerName"?: "..."}
///     If "manualFieldValues" is omitted and the study's GeneratedReport
///     still points at a PDF that exists on disk, that exact file is sent
///     straight to the printer — no re-render. Otherwise (the operator
///     edited a field, or the saved PDF is missing) the report is
///     rebuilt via ReportRenderService from the saved/edited field
///     values — still only local Patient/Study/ReportTemplate data and
///     template files, never the original DICOM. Either way, logs a new
///     PrintJob row (Type=Report); the study's existing GeneratedReport
///     row and any prior PrintJob rows are never overwritten
///     (IPrintJobRepository only ever inserts).
///
/// This step adds the Settings screen's three IPC messages, all backed by
/// SettingsService (Core) rather than any state kept in this class:
///
///   - "getSettings" -&gt; {} — returns the current AppSettings (SettingsService
///     itself guarantees built-in defaults if nothing was ever saved, so
///     this never fails).
///   - "saveSettings" -&gt; {"settings": {aeTitle, listeningPort, reportTemplatesFolderPath,
///     imageSheetBrandingTemplatePath, printerName, logoPath, hospitalName,
///     address, headerText, footerText}} — persists via SettingsService,
///     THEN — the "restart/rebind if changed" requirement — calls
///     DicomScpListener.Restart(...) if the AE Title/port actually
///     differ from what it's currently running with, so the change takes
///     effect immediately rather than only on the next app launch. A
///     failed rebind (e.g. the new port is taken) does not undo the
///     settings save; it's reported back via "listenerRestarted"/
///     "listenerRunning" instead.
///   - "listPrinters" -&gt; {} — installed Windows printer names (see
///     SilentPrinter.GetInstalledPrinterNames), for the Settings screen's
///     printer dropdown.
///
/// Branding and printer selection from Settings are NOT applied by these
/// three handlers themselves — they take effect the next time an image
/// sheet or report is actually printed, via HandlePrintImageSheet/
/// HandleReprintImages (which build the effective ImageSheetBrandingTemplate
/// as ImageSheetBrandingTemplate.ApplySettings(SettingsService.Get()) instead
/// of only ever using the static JSON file) and every printing handler's
/// ResolvePrinterName (which falls back to Settings' PrinterName when the
/// caller didn't request a specific one).
///
/// Wire format: plain JSON strings via
///   window.chrome.webview.postMessage(json)   (web -> native)
///   CoreWebView2.PostWebMessageAsString(json)  (native -> web)
/// </summary>
public sealed partial class WebViewBridge
{
    private readonly WebView2 _webView;
    private readonly IWin32Window _dialogOwner;
    private readonly DicomFileParser _dicomFileParser;
    private readonly DicomProcessingService _dicomProcessingService;
    private readonly PatientStudyService _patientStudyService;
    private readonly ImageSheetComposer _imageSheetComposer;
    private const string LogCategoryUpload = "DICOM";
    private const string LogCategoryPrint = "PRINT";
    private const string LogCategoryReport = "REPORT";

    private readonly ImageSheetBrandingTemplate _imageSheetBranding;
    private readonly SilentPrinter _silentPrinter;
    private readonly ReportRenderService _reportRenderService;
    private readonly TemplateEngine _templateEngine;
    private readonly IReportTemplateRepository _reportTemplateRepository;
    private readonly IGeneratedReportRepository _generatedReportRepository;
    private readonly IPrintJobRepository _printJobRepository;
    private readonly SettingsService _settingsService;
    private readonly DicomScpListener _dicomScpListener;
    private readonly string _imagesBaseDirectory;
    private readonly string _printPreviewDirectory;

    // Must match MainForm's ImagesVirtualHostName — that's the mapping that
    // makes this host name resolve to data/images-extracted on disk.
    private const string ImagesVirtualHostName = "appimages";

    // Must match MainForm's PrintPreviewVirtualHostName — that's the mapping
    // that makes this host name resolve to data/print-preview on disk.
    // Report preview PDFs are also written here (see
    // HandleComposeReportPreview) — it's the same "scratch preview output"
    // concept as the image-sheet previews, just a different file prefix.
    private const string PrintPreviewVirtualHostName = "printpreview";

    // Must match MainForm's ImageTemplateVirtualHostName — that's the
    // mapping that makes this host name resolve to data/image-templates on
    // disk, so the frontend's live preview (PatientDetailPage.tsx /
    // LiveImageSheetPreview.tsx) can load the cached template background
    // PNG (see ImageTemplateBackground) directly as a CSS background-image,
    // with no per-selection IPC round-trip.
    private const string ImageTemplateVirtualHostName = "imagetemplate";

    /// <summary>
    /// The DICOM-sourced field keys ReportRenderService.BuildAutoFilledFields
    /// auto-fills for every report (PATIENT_NAME/PATIENT_ID/AGE/SEX/DATE).
    /// Must be kept in sync with that private method — used here only to
    /// exclude these keys from "getReportFormFields"'s manual-fields list
    /// (the operator never types these in; the Report Print UI shows them
    /// separately, read-only/editable, sourced from the study record).
    /// </summary>
    private static readonly HashSet<string> AutoFilledFieldNames = new()
    {
        "PATIENT_NAME", "PATIENT_ID", "AGE", "SEX", "DATE"
    };

    public WebViewBridge(
        WebView2 webView,
        IWin32Window dialogOwner,
        DicomFileParser dicomFileParser,
        DicomProcessingService dicomProcessingService,
        PatientStudyService patientStudyService,
        ImageSheetComposer imageSheetComposer,
        ImageSheetBrandingTemplate imageSheetBranding,
        SilentPrinter silentPrinter,
        ReportRenderService reportRenderService,
        TemplateEngine templateEngine,
        IReportTemplateRepository reportTemplateRepository,
        IGeneratedReportRepository generatedReportRepository,
        IPrintJobRepository printJobRepository,
        SettingsService settingsService,
        DicomScpListener dicomScpListener)
    {
        _webView = webView;
        _dialogOwner = dialogOwner;
        _dicomFileParser = dicomFileParser;
        _dicomProcessingService = dicomProcessingService;
        _patientStudyService = patientStudyService;
        _imageSheetComposer = imageSheetComposer;
        _imageSheetBranding = imageSheetBranding;
        _silentPrinter = silentPrinter;
        _reportRenderService = reportRenderService;
        _templateEngine = templateEngine;
        _reportTemplateRepository = reportTemplateRepository;
        _generatedReportRepository = generatedReportRepository;
        _printJobRepository = printJobRepository;
        _settingsService = settingsService;
        _dicomScpListener = dicomScpListener;
        _imagesBaseDirectory = ImageStoragePaths.GetImagesExtractedDirectory();
        _printPreviewDirectory = PrintingPaths.GetPrintPreviewDirectory();
    }

    /// <summary>
    /// Hooks up the WebMessageReceived handler. Must be called after
    /// CoreWebView2 has been initialized (EnsureCoreWebView2Async).
    /// </summary>
    public void Attach()
    {
        _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
    }

    private void OnWebMessageReceived(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        string raw = e.TryGetWebMessageAsString();

        IncomingMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<IncomingMessage>(raw);
        }
        catch (JsonException)
        {
            return;
        }

        if (message is null)
        {
            return;
        }

        switch (message.Type)
        {
            case "ping":
                SendPong();
                break;

            case "uploadDicom":
                HandleUploadDicom();
                break;

            case "inspectDicomFile":
                HandleInspectDicomFile();
                break;

            case "listPatientStudies":
                HandleListPatientStudies();
                break;

            case "searchPatientStudies":
                HandleSearchPatientStudies(message);
                break;

            case "filterPatientStudiesByDateRange":
                HandleFilterPatientStudiesByDateRange(message);
                break;

            case "composeImagePrintPreview":
                HandleComposeImagePrintPreview(message);
                break;

            case "printImageSheet":
                HandlePrintImageSheet(message);
                break;

            case "getReportFormFields":
                HandleGetReportFormFields(message);
                break;

            case "getSavedReportFieldValues":
                HandleGetSavedReportFieldValues(message);
                break;

            case "composeReportPreview":
                HandleComposeReportPreview(message);
                break;

            case "printReport":
                HandlePrintReport(message);
                break;

            case "reprintImages":
                HandleReprintImages(message);
                break;

            case "reprintReport":
                HandleReprintReport(message);
                break;

            case "getSettings":
                HandleGetSettings(message);
                break;

            case "saveSettings":
                HandleSaveSettings(message);
                break;

            case "listPrinters":
                HandleListPrinters(message);
                break;

            case "getImageTemplate":
                HandleGetImageTemplate();
                break;

            case "uploadImageTemplate":
                HandleUploadImageTemplate();
                break;

            case "saveImageTemplateContentArea":
                HandleSaveImageTemplateContentArea(message);
                break;

            case "saveImageTemplatePlaceholders":
                HandleSaveImageTemplatePlaceholders(message);
                break;

            // Report Template Builder (Settings > Report Template) — see WebViewBridge.ReportTemplates.cs.
            case "getReportBuilderTemplates":
            case "saveReportBuilderTemplate":
            case "deleteReportBuilderTemplate":
                HandleReportBuilderTemplateRequest(message.Type, raw);
                break;

            // Settings > DICOM Configuration (machine connection, Test Connection, network info) — see WebViewBridge.DicomConfig.cs.
            case "getDicomMachineConfig":
            case "saveDicomMachineConfig":
            case "testDicomConnection":
            case "getDicomNetworkInfo":
            case "checkDicomMachineStatus":
                HandleDicomConfigRequest(message.Type, raw);
                break;

            // NOTE: additional message types are handled in later sessions.
            default:
                break;
        }
    }

    private void SendPong()
    {
        Send(new OutgoingBridgeMessage("pong"));
    }

    /// <summary>
    /// Prompts the user for a .dcm file via a native dialog, then runs it
    /// through DicomFileParser -> DicomProcessingService and reports the
    /// outcome back to React as a single "uploadDicomResult" message.
    /// </summary>
    private void HandleUploadDicom()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Select a DICOM file",
            Filter = "DICOM files (*.dcm)|*.dcm|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(_dialogOwner) != DialogResult.OK)
        {
            Send(new UploadDicomResult { Success = false, Cancelled = true });
            return;
        }

        try
        {
            DicomParseResult parsed = _dicomFileParser.Parse(dialog.FileName);
            DicomProcessingResult processed = _dicomProcessingService.Process(
                parsed.Patient, parsed.Study, parsed.Image);

            Send(BuildSuccessResult(dialog.FileName, processed));
        }
        catch (DicomParseException ex)
        {
            // Already logged with full technical detail by DicomFileParser,
            // and its Message is written for the operator — surface it
            // verbatim.
            Send(new UploadDicomResult
            {
                Success = false,
                FileName = Path.GetFileName(dialog.FileName),
                Error = ex.Message
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or InvalidOperationException or ArgumentException)
        {
            // A DICOM file missing the identifiers DicomProcessingService
            // requires, or an unreadable/locked file. These messages are
            // already written to be user-facing (see
            // DicomProcessingService.ValidateIdentifiers).
            AppLog.Error(LogCategoryUpload, $"Manual upload of '{dialog.FileName}' failed.", ex);
            Send(new UploadDicomResult
            {
                Success = false,
                FileName = Path.GetFileName(dialog.FileName),
                Error = ex.Message
            });
        }
        catch (Exception ex)
        {
            // Genuinely unexpected — log it as such and show a generic
            // message rather than leaking a raw exception string into the
            // UI or crashing the host.
            AppLog.Error(LogCategoryUpload, $"Unexpected failure importing '{dialog.FileName}'.", ex);
            Send(new UploadDicomResult
            {
                Success = false,
                FileName = Path.GetFileName(dialog.FileName),
                Error = "This file could not be imported. See data/logs/ for details."
            });
        }
    }

    /// <summary>
    /// Prompts for any .dcm file via a native dialog and reports back
    /// every tag it contains (see <see cref="DicomTagInspector"/>) —
    /// purely for browsing what data a file has available; nothing is
    /// written to the database. Backs the Settings screen's "DICOM Data
    /// Check" tool.
    /// </summary>
    private void HandleInspectDicomFile()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Select a DICOM file to inspect",
            Filter = "DICOM files (*.dcm)|*.dcm|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(_dialogOwner) != DialogResult.OK)
        {
            Send(new InspectDicomFileResult
            {
                Success = false,
                Cancelled = true,
                Fields = Array.Empty<DicomFieldInfoDto>()
            });
            return;
        }

        try
        {
            var inspector = new DicomTagInspector();
            DicomInspectionResult result = inspector.Inspect(dialog.FileName);

            Send(new InspectDicomFileResult
            {
                Success = true,
                FileName = result.FileName,
                Fields = result.Fields.Select(f => new DicomFieldInfoDto
                {
                    Tag = f.Tag,
                    Keyword = f.Keyword,
                    Name = f.Name,
                    Vr = f.Vr,
                    Value = f.Value,
                    Category = f.Category,
                    Path = f.Path
                }).ToArray()
            });
        }
        catch (DicomParseException ex)
        {
            // Already logged with full technical detail by DicomTagInspector,
            // and its Message is written for the operator — surface it verbatim.
            Send(new InspectDicomFileResult
            {
                Success = false,
                FileName = Path.GetFileName(dialog.FileName),
                Error = ex.Message,
                Fields = Array.Empty<DicomFieldInfoDto>()
            });
        }
        catch (Exception ex)
        {
            AppLog.Error(LogCategoryUpload, $"Unexpected failure inspecting '{dialog.FileName}'.", ex);
            Send(new InspectDicomFileResult
            {
                Success = false,
                FileName = Path.GetFileName(dialog.FileName),
                Error = "This file could not be inspected. See data/logs/ for details.",
                Fields = Array.Empty<DicomFieldInfoDto>()
            });
        }
    }

    private static UploadDicomResult BuildSuccessResult(string filePath, DicomProcessingResult processed)
    {
        Patient patient = processed.Patient;
        Study study = processed.Study;

        return new UploadDicomResult
        {
            Success = true,
            FileName = Path.GetFileName(filePath),
            PatientId = patient.PatientID,
            PatientName = patient.PatientName,
            StudyInstanceUid = study.StudyInstanceUID,
            ExamType = study.ExamType,
            SopInstanceUid = processed.Image.SOPInstanceUID,
            PatientWasCreated = processed.PatientWasCreated,
            StudyWasCreated = processed.StudyWasCreated,
            ImageWasCreated = processed.ImageWasCreated
        };
    }

    /// <summary>Replies with every study, grouped with its patient and images.</summary>
    private void HandleListPatientStudies()
    {
        RunPatientStudyQuery("listPatientStudiesResult", () => _patientStudyService.GetAll());
    }

    /// <summary>Replies with studies whose patient name/ID matches the request's "searchText".</summary>
    private void HandleSearchPatientStudies(IncomingMessage request)
    {
        RunPatientStudyQuery(
            "searchPatientStudiesResult",
            () => _patientStudyService.Search(request.SearchText ?? string.Empty));
    }

    /// <summary>
    /// Replies with studies whose date falls within the request's
    /// "startDate"/"endDate" (either may be omitted; both are plain
    /// "yyyy-MM-dd" dates, inclusive on both ends).
    /// </summary>
    private void HandleFilterPatientStudiesByDateRange(IncomingMessage request)
    {
        RunPatientStudyQuery("filterPatientStudiesByDateRangeResult", () =>
        {
            DateTime? startDate = ParseDateOrThrow(request.StartDate, "startDate");
            DateTime? endDate = ParseDateOrThrow(request.EndDate, "endDate");
            return _patientStudyService.FilterByDateRange(startDate, endDate);
        });
    }

    /// <summary>
    /// Shared plumbing for the three query handlers above: runs
    /// <paramref name="query"/>, maps a successful result to DTOs and
    /// sends it, or sends a {"success":false,"error":...} reply if it
    /// throws (e.g. an unparsable date).
    /// </summary>
    private void RunPatientStudyQuery(string resultType, Func<IReadOnlyList<PatientStudySummary>> query)
    {
        try
        {
            IReadOnlyList<PatientStudySummary> records = query();
            Send(new PatientStudyListResult
            {
                Type = resultType,
                Success = true,
                Records = records.Select(ToDto).ToList()
            });
        }
        catch (Exception ex)
        {
            Send(new PatientStudyListResult
            {
                Type = resultType,
                Success = false,
                Error = ex.Message
            });
        }
    }

    private PatientStudyRecordDto ToDto(PatientStudySummary summary)
    {
        return new PatientStudyRecordDto
        {
            PatientId = summary.Patient.PatientID,
            PatientName = summary.Patient.PatientName,
            DateOfBirth = summary.Patient.DateOfBirth?.ToString("yyyy-MM-dd"),
            Age = summary.Patient.Age,
            Sex = summary.Patient.Sex,
            StudyInstanceUid = summary.Study.StudyInstanceUID,
            ExamType = summary.Study.ExamType,
            StudyDateTime = summary.Study.StudyDateTime.ToString("o"),
            Status = summary.Study.Status,
            // Drives the Patient List's "Reprint Report" action, which
            // should only be offered when there's actually a saved report
            // to reprint.
            HasGeneratedReport = _generatedReportRepository.GetByStudy(summary.Study.StudyInstanceUID) is not null,
            Images = summary.Images.Select(image => new ImageDto
            {
                SopInstanceUid = image.SOPInstanceUID,
                FilePath = image.FilePath,
                ThumbnailPath = image.ThumbnailPath,
                ImageUrl = TryBuildImageUrl(image.FilePath),
                // Fall back to the full image if no thumbnail was generated
                // for it, so the list still has something to show.
                ThumbnailUrl = TryBuildImageUrl(image.ThumbnailPath) ?? TryBuildImageUrl(image.FilePath)
            }).ToList()
        };
    }

    /// <summary>
    /// Converts an absolute on-disk path under <see cref="_imagesBaseDirectory"/>
    /// into a URL the WebView2 virtual-host mapping can serve (e.g.
    /// "https://appimages/1.2.3/img_1.jpg"). Returns null for a missing path,
    /// or one that (unexpectedly) falls outside that folder — there's no
    /// mapping that covers it, so there's no URL to give the frontend.
    /// </summary>
    private string? TryBuildImageUrl(string? absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
        {
            return null;
        }

        string relative = Path.GetRelativePath(_imagesBaseDirectory, absolutePath);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            return null;
        }

        string urlPath = relative.Replace(Path.DirectorySeparatorChar, '/');
        string encoded = string.Join('/', urlPath.Split('/').Select(Uri.EscapeDataString));
        return $"https://{ImagesVirtualHostName}/{encoded}";
    }

    /// <summary>
    /// Composes the requested images into a PDF under data/print-preview and
    /// replies with a "https://printpreview/..." URL for PrintPreview.tsx to
    /// load into an &lt;iframe&gt;, plus enough metadata (page/image counts,
    /// first-page grid) for the toolbar text above it.
    /// </summary>
    private void HandleComposeImagePrintPreview(IncomingMessage request)
    {
        List<string> imageFilePaths = request.ImageFilePaths ?? new List<string>();

        if (imageFilePaths.Count == 0)
        {
            Send(new ImagePrintPreviewResult { Success = false, Error = "Select at least one image to preview." });
            return;
        }

        try
        {
            string outputPath = Path.Combine(_printPreviewDirectory, $"preview-{Guid.NewGuid():N}.pdf");
            ImageSheetBrandingTemplate branding = CurrentImageSheetBranding();
            (int Columns, int Rows)? gridOverride = ResolveGridOverride(request);
            string? backgroundImagePath = CurrentImageTemplateBackgroundPath();
            ImageSheetComposeResult composeResult = _imageSheetComposer.Compose(imageFilePaths, branding, outputPath, gridOverride, backgroundImagePath, CurrentImageTemplateContentArea(), request.GrayBackground == true, CurrentImageTemplatePlaceholders(), request.PlaceholderValues);

            int columns;
            int rows;
            if (gridOverride.HasValue)
            {
                (columns, rows) = gridOverride.Value;
            }
            else
            {
                int firstPageImageCount = Math.Min(composeResult.ImagesComposed, Math.Max(1, branding.MaxImagesPerPage));
                (columns, rows) = ImageSheetComposer.CalculateGridDimensions(firstPageImageCount);
            }

            Send(new ImagePrintPreviewResult
            {
                Success = true,
                PreviewUrl = TryBuildPrintPreviewUrl(composeResult.OutputPdfPath),
                PageCount = composeResult.PageCount,
                ImagesComposed = composeResult.ImagesComposed,
                SkippedCount = composeResult.SkippedImagePaths.Count,
                Columns = columns,
                Rows = rows
            });
        }
        catch (ImageSheetComposeException ex)
        {
            // Already logged by ImageSheetComposer; Message is operator-facing.
            Send(new ImagePrintPreviewResult { Success = false, Error = ex.Message });
        }
        catch (Exception ex) when (ex is ArgumentException or FileNotFoundException)
        {
            // Expected: no paths supplied, or none of them exist on disk anymore.
            Send(new ImagePrintPreviewResult { Success = false, Error = ex.Message });
        }
        catch (Exception ex)
        {
            AppLog.Error(LogCategoryPrint, "Unexpected failure building the image sheet preview.", ex);
            Send(new ImagePrintPreviewResult { Success = false, Error = "Could not build the preview. See data/logs/ for details." });
        }
    }

    /// <summary>
    /// Composes the requested images the same way as <see cref="HandleComposeImagePrintPreview"/>,
    /// then hands the resulting PDF to <see cref="SilentPrinter"/>. The
    /// reply reports whichever path SilentPrinter actually took (silent
    /// print / shell-verb print / saved-to-disk fallback) — see
    /// <see cref="SilentPrintResult"/>.
    /// </summary>
    private void HandlePrintImageSheet(IncomingMessage request)
    {
        List<string> imageFilePaths = request.ImageFilePaths ?? new List<string>();

        if (imageFilePaths.Count == 0)
        {
            Send(new PrintImageSheetResult { Success = false, Error = "Select at least one image to print." });
            return;
        }

        try
        {
            string outputPath = Path.Combine(_printPreviewDirectory, $"print-{Guid.NewGuid():N}.pdf");
            (int Columns, int Rows)? gridOverride = ResolveGridOverride(request);
            string? backgroundImagePath = CurrentImageTemplateBackgroundPath();
            ImageSheetComposeResult composeResult = _imageSheetComposer.Compose(imageFilePaths, CurrentImageSheetBranding(), outputPath, gridOverride, backgroundImagePath, CurrentImageTemplateContentArea(), request.GrayBackground == true, CurrentImageTemplatePlaceholders(), request.PlaceholderValues);

            string? printerName = ResolvePrinterName(request);
            SilentPrintResult printResult = _silentPrinter.Print(composeResult.OutputPdfPath, printerName);

            // The patient moves from "Waiting Study" to "Image Printed" in the Patient List.
            if (printResult.Succeeded)
            {
                MarkStudyImagePrinted(request.StudyInstanceUid);
            }

            Send(new PrintImageSheetResult
            {
                Success = printResult.Succeeded,
                Mode = printResult.Mode.ToString(),
                PrinterUsed = printResult.PrinterUsed,
                OutputPath = printResult.OutputPath,
                Message = printResult.Message,
                PageCount = composeResult.PageCount,
                ImagesComposed = composeResult.ImagesComposed,
                SkippedCount = composeResult.SkippedImagePaths.Count
            });
        }
        catch (ImageSheetComposeException ex)
        {
            Send(new PrintImageSheetResult { Success = false, Error = ex.Message });
        }
        catch (Exception ex) when (ex is ArgumentException or FileNotFoundException)
        {
            Send(new PrintImageSheetResult { Success = false, Error = ex.Message });
        }
        catch (Exception ex)
        {
            AppLog.Error(LogCategoryPrint, "Unexpected failure printing the image sheet.", ex);
            Send(new PrintImageSheetResult { Success = false, Error = "Could not print the image sheet. See data/logs/ for details." });
        }
    }

    /// <summary>
    /// Replies with the exam type's ReportTemplate's manual field keys —
    /// every <c>{{TOKEN}}</c> placeholder the template contains other than
    /// the auto-filled ones (<see cref="AutoFilledFieldNames"/>). Found by
    /// running the *same* <see cref="TemplateEngine.Merge"/> scan
    /// ReportRenderService uses internally, but with an empty field
    /// dictionary so every token in the template comes back as "missing" —
    /// i.e. a read-only introspection, no merge/render/save side effects.
    /// </summary>
    private void HandleGetReportFormFields(IncomingMessage request)
    {
        string examType = request.ExamType ?? string.Empty;

        try
        {
            ReportTemplate? template = _reportTemplateRepository.GetByExamType(examType);
            if (template is null)
            {
                Send(new ReportFormFieldsResult
                {
                    Success = false,
                    Error = $"No report template is configured for exam type '{examType}'."
                });
                return;
            }

            TemplateMergeResult introspection = _templateEngine.Merge(
                template.HtmlTemplateFilePath, new Dictionary<string, string>());

            List<string> manualFields = introspection.MissingFields
                .Where(field => !AutoFilledFieldNames.Contains(field))
                .ToList();

            Send(new ReportFormFieldsResult { Success = true, ExamType = examType, ManualFields = manualFields });
        }
        catch (FileNotFoundException ex)
        {
            Send(new ReportFormFieldsResult { Success = false, Error = ex.Message });
        }
        catch (TemplateRenderException ex)
        {
            Send(new ReportFormFieldsResult { Success = false, Error = ex.Message });
        }
        catch (Exception ex)
        {
            AppLog.Error(LogCategoryReport, "Unexpected failure loading the report template's fields.", ex);
            Send(new ReportFormFieldsResult { Success = false, Error = "Could not load the report template. See data/logs/ for details." });
        }
    }

    /// <summary>
    /// Replies with the study's existing GeneratedReport field values (both
    /// auto-filled and manual, as last saved), or an empty dictionary if no
    /// report has ever been generated for this study — lets the Report
    /// Print UI resume a previous draft instead of always starting blank.
    /// </summary>
    private void HandleGetSavedReportFieldValues(IncomingMessage request)
    {
        string studyInstanceUid = request.StudyInstanceUid ?? string.Empty;

        try
        {
            GeneratedReport? existing = _generatedReportRepository.GetByStudy(studyInstanceUid);
            Dictionary<string, string> fieldValues = existing is null
                ? new Dictionary<string, string>()
                : JsonSerializer.Deserialize<Dictionary<string, string>>(existing.FieldValuesJson) ?? new Dictionary<string, string>();

            Send(new SavedReportFieldValuesResult { Success = true, FieldValues = fieldValues });
        }
        catch (Exception ex)
        {
            AppLog.Error(LogCategoryReport, "Failed to load a study's saved report field values.", ex);
            Send(new SavedReportFieldValuesResult { Success = false, Error = "Could not load the saved report values. See data/logs/ for details." });
        }
    }

    /// <summary>
    /// Generates the report via <see cref="ReportRenderService"/> into a
    /// scratch file under <see cref="_printPreviewDirectory"/> (same folder
    /// "composeImagePrintPreview" uses) and replies with a
    /// "https://printpreview/..." URL for the &lt;iframe&gt; preview.
    /// Called on every form-field change (debounced client-side), so this
    /// also re-saves the study's GeneratedReport row each time — harmless,
    /// since ReportRenderService always updates the same row rather than
    /// creating a duplicate.
    /// </summary>
    private async void HandleComposeReportPreview(IncomingMessage request)
    {
        string studyInstanceUid = request.StudyInstanceUid ?? string.Empty;
        Dictionary<string, string> manualFieldValues = request.ManualFieldValues ?? new Dictionary<string, string>();

        if (string.IsNullOrWhiteSpace(studyInstanceUid))
        {
            Send(new ReportPreviewResult { Success = false, Error = "No study selected." });
            return;
        }

        try
        {
            string outputPath = Path.Combine(_printPreviewDirectory, $"report-preview-{Guid.NewGuid():N}.pdf");
            ReportRenderOutcome outcome = await _reportRenderService.GenerateReportAsync(
                studyInstanceUid, manualFieldValues, outputPath);

            Send(new ReportPreviewResult
            {
                Success = true,
                PreviewUrl = TryBuildPrintPreviewUrl(outcome.GeneratedReport.GeneratedPdfPath!),
                MissingFields = outcome.MissingFields,
                UnusedFields = outcome.UnusedFields
            });
        }
        catch (TemplateRenderException ex)
        {
            // Template missing, unreadable, or malformed. Already logged
            // with full detail by TemplateEngine/PdfRenderer; Message is
            // written for the operator.
            Send(new ReportPreviewResult { Success = false, Error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            // Expected failure modes ReportRenderService itself documents
            // throwing for: unknown study/patient, or no template configured.
            Send(new ReportPreviewResult { Success = false, Error = ex.Message });
        }
        catch (Exception ex)
        {
            AppLog.Error(LogCategoryReport, "Unexpected failure building the report preview.", ex);
            Send(new ReportPreviewResult { Success = false, Error = "Could not build the report preview. See data/logs/ for details." });
        }
    }

    /// <summary>
    /// Generates the report via <see cref="ReportRenderService"/> into a
    /// stable per-study path under <c>data/generated-reports</c> (so the
    /// "official" printed PDF has a predictable, permanent location rather
    /// than a scratch preview filename), then hands it to the same
    /// <see cref="SilentPrinter"/> instance "printImageSheet" uses.
    /// </summary>
    private async void HandlePrintReport(IncomingMessage request)
    {
        string studyInstanceUid = request.StudyInstanceUid ?? string.Empty;
        Dictionary<string, string> manualFieldValues = request.ManualFieldValues ?? new Dictionary<string, string>();

        if (string.IsNullOrWhiteSpace(studyInstanceUid))
        {
            Send(new PrintReportResult { Success = false, Error = "No study selected." });
            return;
        }

        try
        {
            string outputPath = Path.Combine(
                TemplatingPaths.GetGeneratedReportsScratchDirectory(),
                $"{SanitizeForFileName(studyInstanceUid)}.pdf");

            ReportRenderOutcome outcome = await _reportRenderService.GenerateReportAsync(
                studyInstanceUid, manualFieldValues, outputPath);

            string? printerName = ResolvePrinterName(request);
            SilentPrintResult printResult = _silentPrinter.Print(outcome.GeneratedReport.GeneratedPdfPath!, printerName);

            Send(new PrintReportResult
            {
                Success = printResult.Succeeded,
                Mode = printResult.Mode.ToString(),
                PrinterUsed = printResult.PrinterUsed,
                OutputPath = printResult.OutputPath,
                Message = printResult.Message,
                MissingFields = outcome.MissingFields,
                UnusedFields = outcome.UnusedFields
            });
        }
        catch (TemplateRenderException ex)
        {
            Send(new PrintReportResult { Success = false, Error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            Send(new PrintReportResult { Success = false, Error = ex.Message });
        }
        catch (Exception ex)
        {
            AppLog.Error(LogCategoryReport, "Unexpected failure printing the report.", ex);
            Send(new PrintReportResult { Success = false, Error = "Could not print the report. See data/logs/ for details." });
        }
    }

    /// <summary>Replaces characters that can't appear in a Windows file name (e.g. the dots in a DICOM UID are fine, but be defensive) so a study's report PDF gets a stable, valid file name.</summary>
    private static string SanitizeForFileName(string value)
    {
        foreach (char invalidChar in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalidChar, '_');
        }
        return value;
    }

    /// <summary>
    /// Reprints a study's already-extracted images — the file paths are
    /// looked up from the persisted <see cref="Image"/> rows via
    /// <see cref="PatientStudyService.GetByStudy"/> rather than supplied by
    /// the caller (a Patient List reprint action only identifies the
    /// study, not a fresh selection), so nothing here reads the original
    /// DICOM file again. Composition and printing otherwise work exactly
    /// like <see cref="HandlePrintImageSheet"/>. On success, appends a new
    /// PrintJob row (Type=ImageSheet) — the original print's history (if
    /// any) is untouched, since <see cref="IPrintJobRepository"/> only
    /// ever inserts.
    /// </summary>
    private void HandleReprintImages(IncomingMessage request)
    {
        string studyInstanceUid = request.StudyInstanceUid ?? string.Empty;

        if (string.IsNullOrWhiteSpace(studyInstanceUid))
        {
            Send(new ReprintImagesResult { Success = false, Error = "No study selected." });
            return;
        }

        PatientStudySummary? summary = _patientStudyService.GetByStudy(studyInstanceUid);
        if (summary is null)
        {
            Send(new ReprintImagesResult
            {
                Success = false,
                Error = $"No study found with StudyInstanceUID '{studyInstanceUid}'."
            });
            return;
        }

        List<string> imageFilePaths = summary.Images.Select(image => image.FilePath).ToList();
        if (imageFilePaths.Count == 0)
        {
            Send(new ReprintImagesResult { Success = false, Error = "This study has no stored images to reprint." });
            return;
        }

        try
        {
            string outputPath = Path.Combine(_printPreviewDirectory, $"reprint-image-{Guid.NewGuid():N}.pdf");
            string? backgroundImagePath = CurrentImageTemplateBackgroundPath();
            ImageSheetComposeResult composeResult = _imageSheetComposer.Compose(imageFilePaths, CurrentImageSheetBranding(), outputPath, gridOverride: null, backgroundImagePath: backgroundImagePath, contentArea: CurrentImageTemplateContentArea(), placeholders: CurrentImageTemplatePlaceholders(), placeholderValues: BuildPlaceholderValues(summary));

            string? printerName = ResolvePrinterName(request);
            SilentPrintResult printResult = _silentPrinter.Print(composeResult.OutputPdfPath, printerName);

            LogPrintJob(PrintJobType.ImageSheet, studyInstanceUid, printResult.PrinterUsed ?? printerName);

            if (printResult.Succeeded)
            {
                MarkStudyImagePrinted(studyInstanceUid);
            }

            Send(new ReprintImagesResult
            {
                Success = printResult.Succeeded,
                Mode = printResult.Mode.ToString(),
                PrinterUsed = printResult.PrinterUsed,
                OutputPath = printResult.OutputPath,
                Message = printResult.Message,
                PageCount = composeResult.PageCount,
                ImagesComposed = composeResult.ImagesComposed,
                SkippedCount = composeResult.SkippedImagePaths.Count
            });
        }
        catch (ImageSheetComposeException ex)
        {
            Send(new ReprintImagesResult { Success = false, Error = ex.Message });
        }
        catch (Exception ex) when (ex is ArgumentException or FileNotFoundException)
        {
            // FileNotFoundException here means every stored image path is
            // gone from disk (e.g. images-extracted was wiped by hand) —
            // surface it rather than silently no-op'ing.
            Send(new ReprintImagesResult { Success = false, Error = ex.Message });
        }
        catch (Exception ex)
        {
            AppLog.Error(LogCategoryPrint, "Unexpected failure reprinting images.", ex);
            Send(new ReprintImagesResult { Success = false, Error = "Could not reprint the images. See data/logs/ for details." });
        }
    }

    /// <summary>
    /// Reprints a study's report using ONLY already-persisted data. If the
    /// caller supplied no "manualFieldValues" (the operator didn't edit
    /// anything) and the study's <see cref="GeneratedReport.GeneratedPdfPath"/>
    /// still exists on disk, that exact PDF is sent straight to the
    /// printer — no template merge, no re-render, no DICOM. Otherwise
    /// (the operator edited a field via the reprint form, or the saved
    /// PDF has gone missing from disk) the report is rebuilt via
    /// <see cref="ReportRenderService"/> from the saved/edited field
    /// values — still only local Patient/Study/ReportTemplate rows and
    /// the exam type's template file, never the original DICOM. Either
    /// way, appends a new PrintJob row (Type=Report); the study's existing
    /// GeneratedReport row and prior PrintJob rows are never overwritten.
    /// </summary>
    private async void HandleReprintReport(IncomingMessage request)
    {
        string studyInstanceUid = request.StudyInstanceUid ?? string.Empty;

        if (string.IsNullOrWhiteSpace(studyInstanceUid))
        {
            Send(new ReprintReportResult { Success = false, Error = "No study selected." });
            return;
        }

        GeneratedReport? existing = _generatedReportRepository.GetByStudy(studyInstanceUid);
        if (existing is null)
        {
            Send(new ReprintReportResult
            {
                Success = false,
                Error = "No report has been generated for this study yet."
            });
            return;
        }

        string? printerName = ResolvePrinterName(request);

        try
        {
            string pdfToPrint;
            bool regenerated;

            if (request.ManualFieldValues is null
                && !string.IsNullOrWhiteSpace(existing.GeneratedPdfPath)
                && File.Exists(existing.GeneratedPdfPath))
            {
                // Fast path: nothing was edited and the previously
                // generated PDF is still on disk — reprint it exactly as
                // it was, with no merge/render step at all.
                pdfToPrint = existing.GeneratedPdfPath;
                regenerated = false;
            }
            else
            {
                // Either the operator edited fields (request.ManualFieldValues
                // is set) or the saved PDF is missing — rebuild it from the
                // saved field values (falling back to whatever was last
                // saved for any field the operator didn't touch). Still
                // entirely local data: Patient/Study/ReportTemplate rows
                // and the template file on disk, never the original DICOM.
                Dictionary<string, string> fieldValues = request.ManualFieldValues
                    ?? JsonSerializer.Deserialize<Dictionary<string, string>>(existing.FieldValuesJson)
                    ?? new Dictionary<string, string>();

                string outputPath = Path.Combine(
                    TemplatingPaths.GetGeneratedReportsScratchDirectory(),
                    $"{SanitizeForFileName(studyInstanceUid)}.pdf");

                ReportRenderOutcome outcome = await _reportRenderService.GenerateReportAsync(
                    studyInstanceUid, fieldValues, outputPath);

                pdfToPrint = outcome.GeneratedReport.GeneratedPdfPath!;
                regenerated = true;
            }

            SilentPrintResult printResult = _silentPrinter.Print(pdfToPrint, printerName);

            LogPrintJob(PrintJobType.Report, studyInstanceUid, printResult.PrinterUsed ?? printerName);

            Send(new ReprintReportResult
            {
                Success = printResult.Succeeded,
                Mode = printResult.Mode.ToString(),
                PrinterUsed = printResult.PrinterUsed,
                OutputPath = printResult.OutputPath,
                Message = printResult.Message,
                Regenerated = regenerated
            });
        }
        catch (InvalidOperationException ex)
        {
            // Expected failure modes ReportRenderService itself documents
            // throwing for: unknown study/patient, or no template configured.
            Send(new ReprintReportResult { Success = false, Error = ex.Message });
        }
        catch (Exception ex)
        {
            Send(new ReprintReportResult { Success = false, Error = $"Could not reprint report: {ex.Message}" });
        }
    }

    /// <summary>
    /// Appends a new PrintJob row for a completed reprint. Always an
    /// insert, never an update — see <see cref="IPrintJobRepository"/> —
    /// so this never touches the original print's (or an earlier
    /// reprint's) history.
    /// </summary>
    /// <summary>
    /// Marks the study's images as printed (Study.Status = ImagePrinted) so
    /// the Patient List moves it to the "Image Printed" tab. A failure here
    /// is only logged — the print itself already succeeded and must still be
    /// reported as such.
    /// </summary>
    private void MarkStudyImagePrinted(string? studyInstanceUid)
    {
        if (string.IsNullOrWhiteSpace(studyInstanceUid))
        {
            return;
        }

        try
        {
            _patientStudyService.MarkImagePrinted(studyInstanceUid);
        }
        catch (Exception ex)
        {
            AppLog.Error(LogCategoryPrint, "The images were printed, but the study's status could not be updated.", ex);
        }
    }

    private void LogPrintJob(PrintJobType type, string studyInstanceUid, string? printerUsed)
    {
        _printJobRepository.Add(new PrintJob
        {
            Type = type,
            StudyInstanceUID = studyInstanceUid,
            Timestamp = DateTime.UtcNow,
            PrinterUsed = printerUsed ?? string.Empty
        });
    }

    /// <summary>
    /// The branding template to use for the NEXT composed image sheet:
    /// the static JSON-loaded template (<see cref="_imageSheetBranding"/>
    /// — still supplies the page-layout fields Settings doesn't cover:
    /// PageSize, MarginMillimeters, CellSpacingPoints, MaxImagesPerPage,
    /// ShowImageCaptions) with Settings' branding fields (LogoPath/
    /// HospitalName/Address/HeaderText/FooterText) applied on top — see
    /// <see cref="ImageSheetBrandingTemplate.ApplySettings"/>. Reads
    /// Settings fresh via <see cref="_settingsService"/> on every call
    /// rather than caching, so a branding change made in the Settings
    /// screen affects the very next print/preview — no restart needed.
    /// </summary>
    private ImageSheetBrandingTemplate CurrentImageSheetBranding()
    {
        return _imageSheetBranding.ApplySettings(_settingsService.Get());
    }

    /// <summary>
    /// The explicit (Columns, Rows) grid layout the operator picked from
    /// the frontend's grid-layout dropdown, if any. Both <c>gridColumns</c>
    /// and <c>gridRows</c> must be present and positive — a message with
    /// only one of them, or a non-positive value, is treated the same as
    /// "Auto" (no override), so a malformed request degrades to the
    /// existing automatic near-square grid rather than composing something
    /// nonsensical.
    /// </summary>
    private static (int Columns, int Rows)? ResolveGridOverride(IncomingMessage request)
    {
        if (request.GridColumns is int columns && request.GridRows is int rows && columns > 0 && rows > 0)
        {
            return (columns, rows);
        }

        return null;
    }

    /// <summary>
    /// The printer name a print/reprint call should use: the caller's
    /// explicit choice (<c>request.PrinterName</c>, e.g. a one-off
    /// override from a print dialog) if given, otherwise Settings'
    /// configured <see cref="AppSettings.PrinterName"/>, otherwise null
    /// (<see cref="SilentPrinter"/>'s own meaning for "system default
    /// printer"). Reads Settings fresh on every call, same rationale as
    /// <see cref="CurrentImageSheetBranding"/>.
    /// </summary>
    private string? ResolvePrinterName(IncomingMessage request)
    {
        if (!string.IsNullOrWhiteSpace(request.PrinterName))
        {
            return request.PrinterName;
        }

        string? settingsPrinterName = _settingsService.Get().PrinterName;
        return string.IsNullOrWhiteSpace(settingsPrinterName) ? null : settingsPrinterName;
    }

    /// <summary>Returns the current settings. SettingsService.Get() guarantees built-in defaults if nothing was ever saved, so this never fails.</summary>
    private void HandleGetSettings(IncomingMessage request)
    {
        AppSettings settings = _settingsService.Get();
        Send(new GetSettingsResult { Success = true, Settings = SettingsDto.FromModel(settings) });
    }

    /// <summary>
    /// Persists the submitted settings via <see cref="_settingsService"/>,
    /// then — the "restart/rebind if changed" requirement — restarts
    /// <see cref="_dicomScpListener"/> if the AE Title/port actually
    /// differ from what it's currently running with, so the change takes
    /// effect immediately rather than only on the next app launch.
    ///
    /// A failed rebind (e.g. the new port is already in use) does NOT
    /// undo the settings save — the new values are still what's persisted
    /// and what the next <see cref="HandleGetSettings"/> call returns.
    /// It's reported back via <c>listenerRestarted</c>/<c>listenerRunning</c>
    /// instead, so the Settings screen can show a warning without losing
    /// the operator's edits or silently pretending the listener is fine.
    /// </summary>
    private void HandleSaveSettings(IncomingMessage request)
    {
        if (request.Settings is null)
        {
            Send(new SaveSettingsResult { Success = false, Error = "No settings were supplied." });
            return;
        }

        AppSettings newSettings = request.Settings.ToModel();

        if (string.IsNullOrWhiteSpace(newSettings.AeTitle))
        {
            Send(new SaveSettingsResult { Success = false, Error = "AE Title is required." });
            return;
        }

        if (newSettings.ListeningPort is < 1 or > 65535)
        {
            Send(new SaveSettingsResult { Success = false, Error = "Listening port must be between 1 and 65535." });
            return;
        }

        bool aeOrPortChanged =
            !string.Equals(_dicomScpListener.AeTitle, newSettings.AeTitle, StringComparison.Ordinal)
            || _dicomScpListener.Port != newSettings.ListeningPort;

        _settingsService.Save(newSettings);

        bool listenerRestarted = false;
        if (aeOrPortChanged)
        {
            listenerRestarted = true;
            _dicomScpListener.Restart(newSettings.AeTitle, newSettings.ListeningPort);
        }

        Send(new SaveSettingsResult
        {
            Success = true,
            Settings = SettingsDto.FromModel(newSettings),
            ListenerRestarted = listenerRestarted,
            ListenerRunning = _dicomScpListener.IsRunning,
            // Non-null only when the rebind failed — lets the Settings
            // screen name the actual reason (e.g. "port 11112 may already
            // be in use") instead of a generic "auto-receive is down".
            ListenerError = _dicomScpListener.IsRunning ? null : _dicomScpListener.LastStartError
        });
    }

    /// <summary>Installed Windows printer names, for the Settings screen's printer dropdown.</summary>
    private void HandleListPrinters(IncomingMessage request)
    {
        IReadOnlyList<string> printerNames = SilentPrinter.GetInstalledPrinterNames();
        Send(new ListPrintersResult { Success = true, PrinterNames = printerNames });
    }

    // ---- Image template (PDF) upload — Settings screen ----

    /// <summary>Folder the uploaded image-template PDF (and its cached background PNG) lives in (data/image-templates). Created on first use.</summary>
    private static string GetImageTemplateDirectory() => PrintingPaths.GetImageTemplatesDirectory();

    /// <summary>The currently stored image-template PDF, or null if none has been uploaded. Only one is kept at a time.</summary>
    private static string? FindCurrentImageTemplate()
    {
        return Directory
            .EnumerateFiles(GetImageTemplateDirectory(), "*.pdf")
            .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
            .FirstOrDefault();
    }

    /// <summary>
    /// Resolves the currently stored image template to its cached
    /// background PNG (rasterizing/caching it if needed — see
    /// <see cref="ImageTemplateBackground"/>), for both the live preview URL
    /// and the actual print/preview composition. Null if no template is
    /// stored, or rendering it failed.
    /// </summary>
    private static string? CurrentImageTemplateBackgroundPath()
    {
        return ImageTemplateBackground.EnsureBackgroundImage(FindCurrentImageTemplate());
    }

    /// <summary>
    /// The operator's saved "image area" for the current template (the
    /// rectangle the images are confined to, as page fractions), or null if
    /// none is saved / no template is uploaded — in which case sheets use
    /// their normal margins.
    /// </summary>
    private static ImageTemplateContentArea? CurrentImageTemplateContentArea()
    {
        return FindCurrentImageTemplate() is null ? null : ImageTemplateContentAreaStore.Load();
    }

    /// <summary>
    /// The patient-information boxes the operator placed on the current
    /// template (Settings -> Select Image Area), or an empty list if none
    /// are saved / no template is uploaded.
    /// </summary>
    private static List<ImageTemplatePlaceholder> CurrentImageTemplatePlaceholders()
    {
        return FindCurrentImageTemplate() is null
            ? new List<ImageTemplatePlaceholder>()
            : ImageTemplatePlaceholderStore.Load();
    }

    /// <summary>
    /// The text every placeholder field prints for this study's patient
    /// (field key -> value). Used by "reprintImages", where the host itself
    /// knows the patient; for normal printing the frontend sends the same
    /// values (placeholderFields.ts buildPlaceholderValues) — keep the keys
    /// and the formatting (dd/MM/yyyy, hh:mm AM/PM) identical in both places.
    /// </summary>
    private static Dictionary<string, string> BuildPlaceholderValues(PatientStudySummary summary)
    {
        Patient patient = summary.Patient;
        Study study = summary.Study;
        CultureInfo culture = CultureInfo.InvariantCulture;

        string age = patient.Age?.ToString(culture) ?? string.Empty;
        if (age.Length == 0 && patient.DateOfBirth.HasValue)
        {
            DateTime dob = patient.DateOfBirth.Value.Date;
            DateTime today = DateTime.Today;
            int years = today.Year - dob.Year;
            if (dob > today.AddYears(-years))
            {
                years--;
            }

            age = years >= 0 ? years.ToString(culture) : string.Empty;
        }

        string sex = patient.Sex ?? string.Empty;
        string studyDate = study.StudyDateTime.ToString("dd/MM/yyyy", culture);
        string studyTime = study.StudyDateTime.ToString("hh:mm tt", culture);

        return new Dictionary<string, string>
        {
            ["patientName"] = patient.PatientName ?? string.Empty,
            ["patientId"] = patient.PatientID ?? string.Empty,
            ["age"] = age,
            ["sex"] = sex,
            ["ageSex"] = string.Join(" / ", new[] { age, sex }.Where(part => part.Length > 0)),
            ["dateOfBirth"] = patient.DateOfBirth?.ToString("dd/MM/yyyy", culture) ?? string.Empty,
            ["examType"] = study.ExamType ?? string.Empty,
            ["studyDate"] = studyDate,
            ["studyTime"] = studyTime,
            ["studyDateTime"] = $"{studyDate} {studyTime}",
            ["printDate"] = DateTime.Today.ToString("dd/MM/yyyy", culture),
        };
    }

    /// <summary>
    /// Converts the cached background PNG's absolute path into a
    /// "https://imagetemplate/..." URL the frontend can load directly as a
    /// CSS background-image, with a "?v=" cache-buster derived from the
    /// file's last-write time so replacing the template (same file name)
    /// doesn't keep showing WebView2's cached old image.
    /// </summary>
    private static string? TryBuildImageTemplateBackgroundUrl(string? backgroundImagePath)
    {
        if (string.IsNullOrWhiteSpace(backgroundImagePath) || !File.Exists(backgroundImagePath))
        {
            return null;
        }

        string fileName = Path.GetFileName(backgroundImagePath);
        long version = File.GetLastWriteTimeUtc(backgroundImagePath).Ticks;
        return $"https://{ImageTemplateVirtualHostName}/{Uri.EscapeDataString(fileName)}?v={version}";
    }

    /// <summary>Cheap sanity check: a real PDF starts with the bytes "%PDF-".</summary>
    private static bool LooksLikePdf(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var header = new byte[5];
        int read = stream.Read(header, 0, header.Length);
        return read == header.Length
            && header[0] == (byte)'%' && header[1] == (byte)'P' && header[2] == (byte)'D'
            && header[3] == (byte)'F' && header[4] == (byte)'-';
    }

    /// <summary>Reports which image-template PDF (if any) is currently stored.</summary>
    private void HandleGetImageTemplate()
    {
        try
        {
            string? current = FindCurrentImageTemplate();
            string? backgroundUrl = TryBuildImageTemplateBackgroundUrl(ImageTemplateBackground.EnsureBackgroundImage(current));
            Send(new ImageTemplateResult
            {
                Type = "getImageTemplateResult",
                Success = true,
                HasTemplate = current is not null,
                FileName = current is null ? null : Path.GetFileName(current),
                BackgroundImageUrl = backgroundUrl,
                ContentArea = current is null ? null : ImageTemplateContentAreaStore.Load(),
                Placeholders = current is null ? null : ImageTemplatePlaceholderStore.Load()
            });
        }
        catch (Exception ex)
        {
            AppLog.Error(LogCategoryPrint, "Failed to look up the image template.", ex);
            Send(new ImageTemplateResult
            {
                Type = "getImageTemplateResult",
                Success = false,
                Error = "Could not read the image template. See data/logs/ for details."
            });
        }
    }

    /// <summary>
    /// Prompts for a .pdf via a native dialog and stores it in
    /// data/image-templates as the image template (replacing any previous
    /// one). Storage only for now — nothing prints with it yet.
    /// </summary>
    private void HandleUploadImageTemplate()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Select an image template (PDF)",
            Filter = "PDF files (*.pdf)|*.pdf",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(_dialogOwner) != DialogResult.OK)
        {
            Send(new ImageTemplateResult { Type = "uploadImageTemplateResult", Success = false, Cancelled = true });
            return;
        }

        try
        {
            string source = dialog.FileName;

            if (!LooksLikePdf(source))
            {
                Send(new ImageTemplateResult
                {
                    Type = "uploadImageTemplateResult",
                    Success = false,
                    Error = "The selected file is not a valid PDF."
                });
                return;
            }

            string directory = GetImageTemplateDirectory();
            string destination = Path.Combine(directory, Path.GetFileName(source));

            if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(source, destination, overwrite: true);
            }

            // Keep exactly one template: remove any older PDF in the folder.
            foreach (string other in Directory.EnumerateFiles(directory, "*.pdf").ToList())
            {
                if (string.Equals(Path.GetFullPath(other), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    File.Delete(other);
                }
                catch (Exception ex)
                {
                    AppLog.Error(LogCategoryPrint, $"Could not remove the previous image template '{other}'.", ex);
                }
            }

            File.SetLastWriteTimeUtc(destination, DateTime.UtcNow);

            // Regenerate the background PNG cache right away — rather than
            // lazily on the next preview/print — so the very first time the
            // operator opens Patient Detail (or prints) after uploading, the
            // background is already there with nothing to wait on. Old
            // cache is removed first so a failed re-render never leaves a
            // stale background from the *previous* template being served.
            ImageTemplateBackground.InvalidateCache(destination);
            string? backgroundImagePath = ImageTemplateBackground.EnsureBackgroundImage(destination);

            // A new template PDF has its own header/footer, so the previous
            // template's image area no longer applies — the operator selects
            // a fresh one (Settings -> "Select Image Area").
            ImageTemplateContentAreaStore.Clear();

            Send(new ImageTemplateResult
            {
                Type = "uploadImageTemplateResult",
                Success = true,
                HasTemplate = true,
                FileName = Path.GetFileName(destination),
                BackgroundImageUrl = TryBuildImageTemplateBackgroundUrl(backgroundImagePath)
            });
        }
        catch (Exception ex)
        {
            AppLog.Error(LogCategoryPrint, $"Failed to upload the image template '{dialog.FileName}'.", ex);
            Send(new ImageTemplateResult
            {
                Type = "uploadImageTemplateResult",
                Success = false,
                Error = "The template could not be uploaded. See data/logs/ for details."
            });
        }
    }

    /// <summary>
    /// Saves (or, when the request carries no "contentArea", clears) the
    /// rectangle on the template page that the images are confined to —
    /// see <see cref="ImageTemplateContentArea"/>. Applies immediately to
    /// the live preview and to every print/preview composed afterwards.
    /// </summary>
    private void HandleSaveImageTemplateContentArea(IncomingMessage request)
    {
        try
        {
            if (FindCurrentImageTemplate() is null)
            {
                Send(new ImageTemplateResult
                {
                    Type = "saveImageTemplateContentAreaResult",
                    Success = false,
                    Error = "Upload an image template first."
                });
                return;
            }

            if (request.ContentArea is null)
            {
                ImageTemplateContentAreaStore.Clear();
                Send(new ImageTemplateResult
                {
                    Type = "saveImageTemplateContentAreaResult",
                    Success = true,
                    HasTemplate = true,
                    ContentArea = null
                });
                return;
            }

            ImageTemplateContentArea? normalized = ImageTemplateContentAreaStore.Normalize(request.ContentArea);
            if (normalized is null)
            {
                Send(new ImageTemplateResult
                {
                    Type = "saveImageTemplateContentAreaResult",
                    Success = false,
                    Error = "The selected area is too small. Drag a larger rectangle."
                });
                return;
            }

            ImageTemplateContentAreaStore.Save(normalized);
            Send(new ImageTemplateResult
            {
                Type = "saveImageTemplateContentAreaResult",
                Success = true,
                HasTemplate = true,
                ContentArea = normalized
            });
        }
        catch (Exception ex)
        {
            AppLog.Error(LogCategoryPrint, "Failed to save the image template's image area.", ex);
            Send(new ImageTemplateResult
            {
                Type = "saveImageTemplateContentAreaResult",
                Success = false,
                Error = "The image area could not be saved. See data/logs/ for details."
            });
        }
    }

    /// <summary>
    /// Saves the patient-information boxes (name, ID, age... placed on the
    /// template page) sent as "placeholders", replacing the previous list; an
    /// empty/absent list removes them all — see
    /// <see cref="ImageTemplatePlaceholder"/>. Applies immediately to the
    /// live preview and to every print composed afterwards.
    /// </summary>
    private void HandleSaveImageTemplatePlaceholders(IncomingMessage request)
    {
        try
        {
            if (FindCurrentImageTemplate() is null)
            {
                Send(new ImageTemplateResult
                {
                    Type = "saveImageTemplatePlaceholdersResult",
                    Success = false,
                    Error = "Upload an image template first."
                });
                return;
            }

            List<ImageTemplatePlaceholder> normalized = ImageTemplatePlaceholderStore.Normalize(request.Placeholders);
            ImageTemplatePlaceholderStore.Save(normalized);

            Send(new ImageTemplateResult
            {
                Type = "saveImageTemplatePlaceholdersResult",
                Success = true,
                HasTemplate = true,
                Placeholders = normalized
            });
        }
        catch (Exception ex)
        {
            AppLog.Error(LogCategoryPrint, "Failed to save the image template's patient information boxes.", ex);
            Send(new ImageTemplateResult
            {
                Type = "saveImageTemplatePlaceholdersResult",
                Success = false,
                Error = "The patient information boxes could not be saved. See data/logs/ for details."
            });
        }
    }

    /// <summary>
    /// Converts an absolute on-disk path under <see cref="_printPreviewDirectory"/>
    /// into a URL the WebView2 virtual-host mapping can serve (e.g.
    /// "https://printpreview/preview-abc123.pdf"). Returns null for a path
    /// that (unexpectedly) falls outside that folder.
    /// </summary>
    private string? TryBuildPrintPreviewUrl(string absolutePath)
    {
        string relative = Path.GetRelativePath(_printPreviewDirectory, absolutePath);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            return null;
        }

        string urlPath = relative.Replace(Path.DirectorySeparatorChar, '/');
        string encoded = string.Join('/', urlPath.Split('/').Select(Uri.EscapeDataString));
        return $"https://{PrintPreviewVirtualHostName}/{encoded}";
    }

    /// <summary>Parses a plain "yyyy-MM-dd" (or null/blank) date, throwing a user-facing message if it's present but malformed.</summary>
    private static DateTime? ParseDateOrThrow(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed))
        {
            return parsed;
        }

        throw new FormatException($"'{fieldName}' is not a valid date: \"{value}\". Use yyyy-MM-dd.");
    }

    private void Send<T>(T message)
    {
        string json = JsonSerializer.Serialize(message);
        _webView.CoreWebView2.PostWebMessageAsString(json);
    }

    /// <summary>Shape of every incoming request. Unused fields for a given message type are simply left null.</summary>
    private sealed class IncomingMessage
    {
        [JsonPropertyName("type")]
        public string Type { get; init; } = string.Empty;

        [JsonPropertyName("searchText")]
        public string? SearchText { get; init; }

        [JsonPropertyName("startDate")]
        public string? StartDate { get; init; }

        [JsonPropertyName("endDate")]
        public string? EndDate { get; init; }

        [JsonPropertyName("imageFilePaths")]
        public List<string>? ImageFilePaths { get; init; }

        /// <summary>
        /// Optional explicit grid layout for "composeImagePrintPreview" /
        /// "printImageSheet" (e.g. the operator's chosen "3 × 2" from the
        /// frontend's grid-layout dropdown). Both must be present and
        /// positive to take effect; otherwise the automatic near-square
        /// grid (<see cref="ImageSheetComposer.CalculateGridDimensions"/>)
        /// is used, same as before this existed.
        /// </summary>
        [JsonPropertyName("gridColumns")]
        public int? GridColumns { get; init; }

        [JsonPropertyName("gridRows")]
        public int? GridRows { get; init; }

        /// <summary>
        /// For "printImageSheet" / "composeImagePrintPreview": true prints
        /// each image on a light-grey box (the preview's "Gray" option).
        /// Only has an effect when an image area is selected on the
        /// template; absent/false = clean, no grey behind the images.
        /// </summary>
        [JsonPropertyName("grayBackground")]
        public bool? GrayBackground { get; init; }

        [JsonPropertyName("printerName")]
        public string? PrinterName { get; init; }

        [JsonPropertyName("examType")]
        public string? ExamType { get; init; }

        [JsonPropertyName("studyInstanceUid")]
        public string? StudyInstanceUid { get; init; }

        [JsonPropertyName("manualFieldValues")]
        public Dictionary<string, string>? ManualFieldValues { get; init; }

        [JsonPropertyName("settings")]
        public SettingsDto? Settings { get; init; }

        /// <summary>
        /// For "saveImageTemplateContentArea": the rectangle (page
        /// fractions) images are confined to on the template page. Absent /
        /// null means "clear the saved area".
        /// </summary>
        [JsonPropertyName("contentArea")]
        public ImageTemplateContentArea? ContentArea { get; init; }

        /// <summary>
        /// For "saveImageTemplatePlaceholders": the patient-information
        /// boxes placed on the template page. Absent / empty means "remove
        /// them all".
        /// </summary>
        [JsonPropertyName("placeholders")]
        public List<ImageTemplatePlaceholder>? Placeholders { get; init; }

        /// <summary>
        /// For "printImageSheet" / "composeImagePrintPreview": the text each
        /// placeholder field prints for the current patient (field key ->
        /// value, e.g. "patientName" -> "MAHMUDA AKTER"). Absent = no
        /// patient information is printed.
        /// </summary>
        [JsonPropertyName("placeholderValues")]
        public Dictionary<string, string>? PlaceholderValues { get; init; }
    }

    private sealed record OutgoingBridgeMessage(string Type);

    /// <summary>Reply payload for "listPatientStudies" / "searchPatientStudies" / "filterPatientStudiesByDateRange".</summary>
    private sealed class PatientStudyListResult
    {
        [JsonPropertyName("type")]
        public required string Type { get; init; }

        [JsonPropertyName("success")]
        public required bool Success { get; init; }

        [JsonPropertyName("error")]
        public string? Error { get; init; }

        [JsonPropertyName("records")]
        public IReadOnlyList<PatientStudyRecordDto>? Records { get; init; }
    }

    /// <summary>One row: a study, its patient, and its images — the shape "records" entries take.</summary>
    private sealed class PatientStudyRecordDto
    {
        [JsonPropertyName("patientId")]
        public required string PatientId { get; init; }

        [JsonPropertyName("patientName")]
        public required string PatientName { get; init; }

        [JsonPropertyName("dateOfBirth")]
        public string? DateOfBirth { get; init; }

        [JsonPropertyName("age")]
        public int? Age { get; init; }

        [JsonPropertyName("sex")]
        public string? Sex { get; init; }

        [JsonPropertyName("studyInstanceUid")]
        public required string StudyInstanceUid { get; init; }

        [JsonPropertyName("examType")]
        public required string ExamType { get; init; }

        [JsonPropertyName("studyDateTime")]
        public required string StudyDateTime { get; init; }

        [JsonPropertyName("status")]
        public required string Status { get; init; }

        /// <summary>Whether a GeneratedReport row already exists for this study — gates the "Reprint Report" action in the Patient List.</summary>
        [JsonPropertyName("hasGeneratedReport")]
        public required bool HasGeneratedReport { get; init; }

        [JsonPropertyName("images")]
        public required IReadOnlyList<ImageDto> Images { get; init; }
    }

    private sealed class ImageDto
    {
        [JsonPropertyName("sopInstanceUid")]
        public required string SopInstanceUid { get; init; }

        [JsonPropertyName("filePath")]
        public required string FilePath { get; init; }

        [JsonPropertyName("thumbnailPath")]
        public string? ThumbnailPath { get; init; }

        /// <summary>Browser-loadable URL for the full image (served via the "appimages" virtual host), or null if it couldn't be resolved.</summary>
        [JsonPropertyName("imageUrl")]
        public string? ImageUrl { get; init; }

        /// <summary>Browser-loadable URL for the thumbnail (falls back to <see cref="ImageUrl"/> if no thumbnail exists), or null if neither could be resolved.</summary>
        [JsonPropertyName("thumbnailUrl")]
        public string? ThumbnailUrl { get; init; }
    }

    /// <summary>Reply payload for the "uploadDicom" request.</summary>
    private sealed class UploadDicomResult
    {
        [JsonPropertyName("type")]
        public string Type { get; } = "uploadDicomResult";

        [JsonPropertyName("success")]
        public required bool Success { get; init; }

        [JsonPropertyName("cancelled")]
        public bool Cancelled { get; init; }

        [JsonPropertyName("error")]
        public string? Error { get; init; }

        [JsonPropertyName("fileName")]
        public string? FileName { get; init; }

        [JsonPropertyName("patientId")]
        public string? PatientId { get; init; }

        [JsonPropertyName("patientName")]
        public string? PatientName { get; init; }

        [JsonPropertyName("studyInstanceUid")]
        public string? StudyInstanceUid { get; init; }

        [JsonPropertyName("examType")]
        public string? ExamType { get; init; }

        [JsonPropertyName("sopInstanceUid")]
        public string? SopInstanceUid { get; init; }

        [JsonPropertyName("patientWasCreated")]
        public bool PatientWasCreated { get; init; }

        [JsonPropertyName("studyWasCreated")]
        public bool StudyWasCreated { get; init; }

        [JsonPropertyName("imageWasCreated")]
        public bool ImageWasCreated { get; init; }
    }

    /// <summary>Reply payload for the "getImageTemplate" / "uploadImageTemplate" requests (Type says which).</summary>
    private sealed class ImageTemplateResult
    {
        [JsonPropertyName("type")]
        public required string Type { get; init; }

        [JsonPropertyName("success")]
        public required bool Success { get; init; }

        [JsonPropertyName("cancelled")]
        public bool Cancelled { get; init; }

        [JsonPropertyName("error")]
        public string? Error { get; init; }

        [JsonPropertyName("hasTemplate")]
        public bool HasTemplate { get; init; }

        [JsonPropertyName("fileName")]
        public string? FileName { get; init; }

        /// <summary>
        /// "https://imagetemplate/...?v=..." URL for the cached, rasterized
        /// background image (see <see cref="ImageTemplateBackground"/>), or
        /// null if there's no template / it couldn't be rendered. The
        /// frontend loads this directly as a CSS background-image behind
        /// the live print-preview grid.
        /// </summary>
        [JsonPropertyName("backgroundImageUrl")]
        public string? BackgroundImageUrl { get; init; }

        /// <summary>
        /// The saved image area for the current template (page fractions),
        /// or null if none is saved. Also echoed back by
        /// "saveImageTemplateContentArea".
        /// </summary>
        [JsonPropertyName("contentArea")]
        public ImageTemplateContentArea? ContentArea { get; init; }

        /// <summary>
        /// The saved patient-information boxes for the current template, or
        /// null if there's no template. Also echoed back by
        /// "saveImageTemplatePlaceholders".
        /// </summary>
        [JsonPropertyName("placeholders")]
        public List<ImageTemplatePlaceholder>? Placeholders { get; init; }
    }

    /// <summary>Reply payload for the "inspectDicomFile" request.</summary>
    private sealed class InspectDicomFileResult
    {
        [JsonPropertyName("type")]
        public string Type { get; } = "inspectDicomFileResult";

        [JsonPropertyName("success")]
        public required bool Success { get; init; }

        [JsonPropertyName("cancelled")]
        public bool Cancelled { get; init; }

        [JsonPropertyName("error")]
        public string? Error { get; init; }

        [JsonPropertyName("fileName")]
        public string? FileName { get; init; }

        [JsonPropertyName("fields")]
        public required IReadOnlyList<DicomFieldInfoDto> Fields { get; init; }
    }

    /// <summary>One tag as reported by DicomTagInspector — see DicomFieldInfo for field meanings.</summary>
    private sealed class DicomFieldInfoDto
    {
        [JsonPropertyName("tag")]
        public required string Tag { get; init; }

        [JsonPropertyName("keyword")]
        public required string Keyword { get; init; }

        [JsonPropertyName("name")]
        public required string Name { get; init; }

        [JsonPropertyName("vr")]
        public required string Vr { get; init; }

        [JsonPropertyName("value")]
        public required string Value { get; init; }

        [JsonPropertyName("category")]
        public required string Category { get; init; }

        [JsonPropertyName("path")]
        public required string Path { get; init; }
    }

    /// <summary>Reply payload for the "composeImagePrintPreview" request.</summary>
    private sealed class ImagePrintPreviewResult
    {
        [JsonPropertyName("type")]
        public string Type { get; } = "composeImagePrintPreviewResult";

        [JsonPropertyName("success")]
        public required bool Success { get; init; }

        [JsonPropertyName("error")]
        public string? Error { get; init; }

        /// <summary>"https://printpreview/..." URL for the composed PDF, loadable directly into an &lt;iframe&gt;.</summary>
        [JsonPropertyName("previewUrl")]
        public string? PreviewUrl { get; init; }

        [JsonPropertyName("pageCount")]
        public int? PageCount { get; init; }

        [JsonPropertyName("imagesComposed")]
        public int? ImagesComposed { get; init; }

        /// <summary>Requested paths that no longer exist on disk and were silently omitted.</summary>
        [JsonPropertyName("skippedCount")]
        public int? SkippedCount { get; init; }

        /// <summary>Grid dimensions used on the first page — for display only; later pages may differ if more images were selected than fit on one page.</summary>
        [JsonPropertyName("columns")]
        public int? Columns { get; init; }

        [JsonPropertyName("rows")]
        public int? Rows { get; init; }
    }

    /// <summary>Reply payload for the "printImageSheet" request.</summary>
    private sealed class PrintImageSheetResult
    {
        [JsonPropertyName("type")]
        public string Type { get; } = "printImageSheetResult";

        /// <summary>Mirrors SilentPrintResult.Succeeded — true even for the saved-to-disk fallback, since that path completed successfully (just not via an actual printer).</summary>
        [JsonPropertyName("success")]
        public required bool Success { get; init; }

        [JsonPropertyName("error")]
        public string? Error { get; init; }

        /// <summary>One of SilentPrintMode's names: SilentlyPrintedViaHelper / SilentlyPrintedViaShellVerb / SavedToDiskFallback.</summary>
        [JsonPropertyName("mode")]
        public string? Mode { get; init; }

        [JsonPropertyName("printerUsed")]
        public string? PrinterUsed { get; init; }

        /// <summary>Path to the composed PDF (and, for the fallback mode, where it was saved).</summary>
        [JsonPropertyName("outputPath")]
        public string? OutputPath { get; init; }

        [JsonPropertyName("message")]
        public string? Message { get; init; }

        [JsonPropertyName("pageCount")]
        public int? PageCount { get; init; }

        [JsonPropertyName("imagesComposed")]
        public int? ImagesComposed { get; init; }

        [JsonPropertyName("skippedCount")]
        public int? SkippedCount { get; init; }
    }

    /// <summary>Reply payload for the "getReportFormFields" request.</summary>
    private sealed class ReportFormFieldsResult
    {
        [JsonPropertyName("type")]
        public string Type { get; } = "getReportFormFieldsResult";

        [JsonPropertyName("success")]
        public required bool Success { get; init; }

        [JsonPropertyName("error")]
        public string? Error { get; init; }

        [JsonPropertyName("examType")]
        public string? ExamType { get; init; }

        /// <summary>Placeholder field keys the template needs beyond the auto-filled ones (e.g. "LIVER_SIZE", "IMPRESSION") — what ReportForm.tsx should render inputs for.</summary>
        [JsonPropertyName("manualFields")]
        public IReadOnlyList<string>? ManualFields { get; init; }
    }

    /// <summary>Reply payload for the "getSavedReportFieldValues" request.</summary>
    private sealed class SavedReportFieldValuesResult
    {
        [JsonPropertyName("type")]
        public string Type { get; } = "getSavedReportFieldValuesResult";

        [JsonPropertyName("success")]
        public required bool Success { get; init; }

        [JsonPropertyName("error")]
        public string? Error { get; init; }

        [JsonPropertyName("fieldValues")]
        public IReadOnlyDictionary<string, string>? FieldValues { get; init; }
    }

    /// <summary>Reply payload for the "composeReportPreview" request.</summary>
    private sealed class ReportPreviewResult
    {
        [JsonPropertyName("type")]
        public string Type { get; } = "composeReportPreviewResult";

        [JsonPropertyName("success")]
        public required bool Success { get; init; }

        [JsonPropertyName("error")]
        public string? Error { get; init; }

        /// <summary>"https://printpreview/...&quot; URL for the composed report PDF, loadable directly into an &lt;iframe&gt;.</summary>
        [JsonPropertyName("previewUrl")]
        public string? PreviewUrl { get; init; }

        /// <summary>Template placeholder tokens with no matching value — surfaced so the UI can flag an incomplete report before printing.</summary>
        [JsonPropertyName("missingFields")]
        public IReadOnlyList<string>? MissingFields { get; init; }

        [JsonPropertyName("unusedFields")]
        public IReadOnlyList<string>? UnusedFields { get; init; }
    }

    /// <summary>Reply payload for the "printReport" request.</summary>
    private sealed class PrintReportResult
    {
        [JsonPropertyName("type")]
        public string Type { get; } = "printReportResult";

        /// <summary>Mirrors SilentPrintResult.Succeeded — true even for the saved-to-disk fallback.</summary>
        [JsonPropertyName("success")]
        public required bool Success { get; init; }

        [JsonPropertyName("error")]
        public string? Error { get; init; }

        /// <summary>One of SilentPrintMode's names: SilentlyPrintedViaHelper / SilentlyPrintedViaShellVerb / SavedToDiskFallback.</summary>
        [JsonPropertyName("mode")]
        public string? Mode { get; init; }

        [JsonPropertyName("printerUsed")]
        public string? PrinterUsed { get; init; }

        /// <summary>Path to the generated report PDF (and, for the fallback mode, where it was saved).</summary>
        [JsonPropertyName("outputPath")]
        public string? OutputPath { get; init; }

        [JsonPropertyName("message")]
        public string? Message { get; init; }

        [JsonPropertyName("missingFields")]
        public IReadOnlyList<string>? MissingFields { get; init; }

        [JsonPropertyName("unusedFields")]
        public IReadOnlyList<string>? UnusedFields { get; init; }
    }

    /// <summary>Reply payload for the "reprintImages" request.</summary>
    private sealed class ReprintImagesResult
    {
        [JsonPropertyName("type")]
        public string Type { get; } = "reprintImagesResult";

        /// <summary>Mirrors SilentPrintResult.Succeeded — true even for the saved-to-disk fallback.</summary>
        [JsonPropertyName("success")]
        public required bool Success { get; init; }

        [JsonPropertyName("error")]
        public string? Error { get; init; }

        /// <summary>One of SilentPrintMode's names: SilentlyPrintedViaHelper / SilentlyPrintedViaShellVerb / SavedToDiskFallback.</summary>
        [JsonPropertyName("mode")]
        public string? Mode { get; init; }

        [JsonPropertyName("printerUsed")]
        public string? PrinterUsed { get; init; }

        /// <summary>Path to the composed PDF (and, for the fallback mode, where it was saved).</summary>
        [JsonPropertyName("outputPath")]
        public string? OutputPath { get; init; }

        [JsonPropertyName("message")]
        public string? Message { get; init; }

        [JsonPropertyName("pageCount")]
        public int? PageCount { get; init; }

        [JsonPropertyName("imagesComposed")]
        public int? ImagesComposed { get; init; }

        [JsonPropertyName("skippedCount")]
        public int? SkippedCount { get; init; }
    }

    /// <summary>Reply payload for the "reprintReport" request.</summary>
    private sealed class ReprintReportResult
    {
        [JsonPropertyName("type")]
        public string Type { get; } = "reprintReportResult";

        /// <summary>Mirrors SilentPrintResult.Succeeded — true even for the saved-to-disk fallback.</summary>
        [JsonPropertyName("success")]
        public required bool Success { get; init; }

        [JsonPropertyName("error")]
        public string? Error { get; init; }

        /// <summary>One of SilentPrintMode's names: SilentlyPrintedViaHelper / SilentlyPrintedViaShellVerb / SavedToDiskFallback.</summary>
        [JsonPropertyName("mode")]
        public string? Mode { get; init; }

        [JsonPropertyName("printerUsed")]
        public string? PrinterUsed { get; init; }

        /// <summary>Path to the PDF that was actually sent to the printer (and, for the fallback mode, where it was saved).</summary>
        [JsonPropertyName("outputPath")]
        public string? OutputPath { get; init; }

        [JsonPropertyName("message")]
        public string? Message { get; init; }

        /// <summary>False if the previously-generated PDF was reprinted as-is; true if it had to be rebuilt first (edited fields, or the saved PDF was missing from disk).</summary>
        [JsonPropertyName("regenerated")]
        public bool Regenerated { get; init; }
    }

    /// <summary>
    /// Wire shape for <see cref="AppSettings"/> — used both as the
    /// "settings" payload of an incoming "saveSettings" request and as
    /// the "settings" field of a "getSettingsResult"/"saveSettingsResult"
    /// reply, since both sides need exactly the same fields.
    /// </summary>
    private sealed class SettingsDto
    {
        [JsonPropertyName("aeTitle")]
        public string AeTitle { get; init; } = string.Empty;

        [JsonPropertyName("listeningPort")]
        public int ListeningPort { get; init; }

        [JsonPropertyName("reportTemplatesFolderPath")]
        public string? ReportTemplatesFolderPath { get; init; }

        [JsonPropertyName("imageSheetBrandingTemplatePath")]
        public string? ImageSheetBrandingTemplatePath { get; init; }

        [JsonPropertyName("printerName")]
        public string? PrinterName { get; init; }

        [JsonPropertyName("logoPath")]
        public string? LogoPath { get; init; }

        [JsonPropertyName("hospitalName")]
        public string HospitalName { get; init; } = string.Empty;

        [JsonPropertyName("address")]
        public string Address { get; init; } = string.Empty;

        [JsonPropertyName("headerText")]
        public string? HeaderText { get; init; }

        [JsonPropertyName("footerText")]
        public string? FooterText { get; init; }

        public static SettingsDto FromModel(AppSettings settings) => new()
        {
            AeTitle = settings.AeTitle,
            ListeningPort = settings.ListeningPort,
            ReportTemplatesFolderPath = settings.ReportTemplatesFolderPath,
            ImageSheetBrandingTemplatePath = settings.ImageSheetBrandingTemplatePath,
            PrinterName = settings.PrinterName,
            LogoPath = settings.LogoPath,
            HospitalName = settings.HospitalName,
            Address = settings.Address,
            HeaderText = settings.HeaderText,
            FooterText = settings.FooterText,
        };

        public AppSettings ToModel() => new()
        {
            AeTitle = AeTitle,
            ListeningPort = ListeningPort,
            ReportTemplatesFolderPath = ReportTemplatesFolderPath,
            ImageSheetBrandingTemplatePath = ImageSheetBrandingTemplatePath,
            PrinterName = PrinterName,
            LogoPath = LogoPath,
            HospitalName = HospitalName,
            Address = Address,
            HeaderText = HeaderText,
            FooterText = FooterText,
        };
    }

    /// <summary>Reply payload for the "getSettings" request.</summary>
    private sealed class GetSettingsResult
    {
        [JsonPropertyName("type")]
        public string Type { get; } = "getSettingsResult";

        [JsonPropertyName("success")]
        public required bool Success { get; init; }

        [JsonPropertyName("error")]
        public string? Error { get; init; }

        [JsonPropertyName("settings")]
        public SettingsDto? Settings { get; init; }
    }

    /// <summary>Reply payload for the "saveSettings" request.</summary>
    private sealed class SaveSettingsResult
    {
        [JsonPropertyName("type")]
        public string Type { get; } = "saveSettingsResult";

        [JsonPropertyName("success")]
        public required bool Success { get; init; }

        [JsonPropertyName("error")]
        public string? Error { get; init; }

        [JsonPropertyName("settings")]
        public SettingsDto? Settings { get; init; }

        /// <summary>True if the AE Title/port actually changed and DicomScpListener.Restart was called.</summary>
        [JsonPropertyName("listenerRestarted")]
        public bool ListenerRestarted { get; init; }

        /// <summary>DicomScpListener.IsRunning after saving (and any restart attempt). False means auto-receive is currently down — e.g. the new port is already in use — even though the save itself succeeded.</summary>
        [JsonPropertyName("listenerRunning")]
        public bool ListenerRunning { get; init; }

        /// <summary>Why the listener isn't running, when <see cref="ListenerRunning"/> is false. Null when it's running fine.</summary>
        [JsonPropertyName("listenerError")]
        public string? ListenerError { get; init; }
    }

    /// <summary>Reply payload for the "listPrinters" request.</summary>
    private sealed class ListPrintersResult
    {
        [JsonPropertyName("type")]
        public string Type { get; } = "listPrintersResult";

        [JsonPropertyName("success")]
        public required bool Success { get; init; }

        [JsonPropertyName("error")]
        public string? Error { get; init; }

        [JsonPropertyName("printerNames")]
        public IReadOnlyList<string>? PrinterNames { get; init; }
    }
}
