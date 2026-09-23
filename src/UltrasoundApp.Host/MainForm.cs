using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using UltrasoundApp.Core.Repositories;
using UltrasoundApp.Core.Services;
using UltrasoundApp.Dicom;
using UltrasoundApp.Printing;
using UltrasoundApp.Templating;

namespace UltrasoundApp.Host;

/// <summary>
/// Main (and only) window for the app shell. Hosts a single, full-window
/// WebView2 control that renders the React frontend. No business logic
/// lives here — this is purely the native shell + IPC wiring.
/// </summary>
public partial class MainForm : Form
{
    private const string VirtualHostName = "appassets";

    // Serves data/images-extracted/* as https://appimages/... so <img> tags
    // in React can load thumbnails/images. Chromium blocks file:// src on
    // pages loaded over http/https (both the Vite dev server and the
    // packaged wwwroot are http/https), so a virtual-host mapping is the
    // only way to display them — this mapping is independent of which
    // document is loaded, so it applies in both DEBUG and RELEASE.
    // WebViewBridge.cs builds URLs against this same host name.
    private const string ImagesVirtualHostName = "appimages";

    // Serves data/print-preview/* as https://printpreview/... so
    // PrintPreview.tsx can load a composed image-sheet PDF straight into
    // an <iframe> (WebView2 renders PDFs natively). Same rationale as
    // ImagesVirtualHostName above — WebViewBridge.cs builds URLs against
    // this same host name and must be kept in sync with it.
    private const string PrintPreviewVirtualHostName = "printpreview";

    // Serves data/image-templates/* as https://imagetemplate/... so the
    // Patient Detail page's live print-preview can load the cached,
    // rasterized "Image Template" background PNG (see
    // ImageTemplateBackground) as a plain CSS background-image — instant,
    // no IPC round-trip per selection change. WebViewBridge.cs builds URLs
    // against this same host name and must be kept in sync with it.
    private const string ImageTemplateVirtualHostName = "imagetemplate";

    // In DEBUG we point at the Vite dev server so `npm run dev` gives
    // hot-reload. In RELEASE we load the built static files that were
    // copied into wwwroot/ next to the executable (see CopyFrontendBuild
    // target in UltrasoundApp.Host.csproj).
#if DEBUG
    private const string StartUrl = "http://localhost:5173/";
#else
    private static readonly string StartUrl = $"https://{VirtualHostName}/index.html";
#endif

    private readonly DicomFileParser _dicomFileParser;
    private readonly DicomProcessingService _dicomProcessingService;
    private readonly PatientStudyService _patientStudyService;
    private readonly ImageSheetComposer _imageSheetComposer;
    private readonly ImageSheetBrandingTemplate _imageSheetBranding;
    private readonly SilentPrinter _silentPrinter;
    private readonly ReportRenderService _reportRenderService;
    private readonly TemplateEngine _templateEngine;
    private readonly IReportTemplateRepository _reportTemplateRepository;
    private readonly IGeneratedReportRepository _generatedReportRepository;
    private readonly IPrintJobRepository _printJobRepository;
    private readonly SettingsService _settingsService;
    private readonly DicomScpListener _dicomScpListener;

    private WebView2 _webView = null!;
    private WebViewBridge _bridge = null!;

    public MainForm(
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

        InitializeComponent();
        InitializeWebView();
    }

    private void InitializeComponent()
    {
        SuspendLayout();

        // Normal resizable window with the standard title bar: minimize /
        // maximize-restore / close buttons, drag to move, drag edges to
        // resize. Starts maximized; the operator can restore it and make it
        // any size (down to MinimumSize).
        FormBorderStyle = FormBorderStyle.Sizable;
        ControlBox = true;
        MinimizeBox = true;
        MaximizeBox = true;
        WindowState = FormWindowState.Maximized;
        StartPosition = FormStartPosition.CenterScreen;
        Text = "Ultrasound Reporting";
        MinimumSize = new Size(1000, 640);

        _webView = new WebView2
        {
            Dock = DockStyle.Fill
        };
        Controls.Add(_webView);

        ClientSize = new Size(1280, 800); // size used when restored from maximized
        Name = "MainForm";

        ResumeLayout(false);
    }

    private void InitializeWebView()
    {
        _bridge = new WebViewBridge(
            _webView,
            this,
            _dicomFileParser,
            _dicomProcessingService,
            _patientStudyService,
            _imageSheetComposer,
            _imageSheetBranding,
            _silentPrinter,
            _reportRenderService,
            _templateEngine,
            _reportTemplateRepository,
            _generatedReportRepository,
            _printJobRepository,
            _settingsService,
            _dicomScpListener);
        Load += async (_, _) => await SetupWebViewAsync();
    }

    private async Task SetupWebViewAsync()
    {
        await _webView.EnsureCoreWebView2Async();

#if !DEBUG
        var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            VirtualHostName,
            wwwroot,
            CoreWebView2HostResourceAccessKind.Allow);
#endif

        _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            ImagesVirtualHostName,
            ImageStoragePaths.GetImagesExtractedDirectory(),
            CoreWebView2HostResourceAccessKind.Allow);

        _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            PrintPreviewVirtualHostName,
            PrintingPaths.GetPrintPreviewDirectory(),
            CoreWebView2HostResourceAccessKind.Allow);

        _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            ImageTemplateVirtualHostName,
            PrintingPaths.GetImageTemplatesDirectory(),
            CoreWebView2HostResourceAccessKind.Allow);

        _bridge.Attach();
        _bridge.StartLiveEvents(); // live heartbeat / machine status / "new study arrived" pushes to the page

        _webView.CoreWebView2.Navigate(StartUrl);
    }
}
