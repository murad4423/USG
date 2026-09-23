using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using UltrasoundApp.Core.Logging;
using UltrasoundApp.Core.Reporting;

namespace UltrasoundApp.Templating;

/// <summary>
/// Page/margin/orientation options for <see cref="PdfRenderer"/>.
/// Dimensions are in inches, matching <see cref="CoreWebView2PrintSettings"/>.
/// Defaults are A4 portrait with a modest margin — appropriate for a
/// clinical report printed on standard hospital printers.
/// </summary>
/// <param name="PageWidthInches">Page width in inches. Default 8.27" (A4).</param>
/// <param name="PageHeightInches">Page height in inches. Default 11.69" (A4).</param>
/// <param name="MarginInches">Uniform top/bottom/left/right margin in inches.</param>
/// <param name="PrintBackgrounds">Whether CSS background colors/images are printed. Report templates rely on this being <c>true</c> for header/footer shading.</param>
/// <param name="LandscapeOrientation">Set <c>true</c> for landscape instead of portrait.</param>
public sealed record PdfRenderOptions(
    double PageWidthInches = 8.27,
    double PageHeightInches = 11.69,
    double MarginInches = 0.4,
    bool PrintBackgrounds = true,
    bool LandscapeOrientation = false);

/// <summary>Outcome of a single <see cref="PdfRenderer.RenderHtmlToPdfAsync"/> call.</summary>
public sealed record PdfRenderResult(bool Succeeded, string? OutputPdfPath, string Message);

/// <summary>
/// Converts merged report HTML into a PDF file using WebView2's headless
/// print-to-PDF API (<see cref="CoreWebView2.PrintToPdfAsync(string, CoreWebView2PrintSettings?)"/>),
/// with no OS print dialog and no visible window.
///
/// <para>
/// <b>Why WebView2 instead of a dedicated PDF library:</b> WebView2 is a
/// full Chromium HTML/CSS renderer, already a dependency of this app (the
/// Host shell), and — critically for this project — its text shaping and
/// font fallback is the same engine Windows/Edge use everywhere else, so
/// Bangla (and any other complex script) renders correctly out of the box
/// using whatever Bangla-capable font is installed (Windows 10/11 ship
/// "Nirmala UI", which covers Bengali). A print-focused PDF library would
/// need its own font-shaping/BiDi/complex-script support layered on top,
/// which is a much larger undertaking than reusing Chromium's.
/// </para>
///
/// <para>
/// <b>Why a hidden WinForms Form + dedicated STA thread:</b> WebView2's
/// <c>CoreWebView2Controller</c> must live on a single-threaded-apartment
/// (STA) thread with a running Win32 message loop (it marshals its async
/// completions through window messages). Rather than requiring every
/// caller to already be pumping messages on an STA thread — the manual
/// test harness is a plain console app, but this will later also be called
/// from the WinForms Host's UI thread — this class spins up and owns its
/// *own* dedicated STA thread with a minimized, off-screen, invisible
/// <see cref="Form"/> hosting the actual <see cref="WebView2"/> control.
/// The form is never shown to the user; it only exists to give WebView2 a
/// window handle and a message pump to run on. Callers just await a Task
/// like any other async API.
/// </para>
///
/// <para>
/// One <see cref="PdfRenderer"/> instance keeps its WebView2 environment
/// alive across multiple <see cref="RenderHtmlToPdfAsync"/> calls (creating
/// a fresh Chromium environment per call would be slow) — reuse the same
/// instance for a batch of reports and <see cref="Dispose"/> it when done.
/// </para>
/// </summary>
public sealed class PdfRenderer : IDisposable, IReportPdfRenderer
{
    private const string LogCategory = "TEMPLATE";

    private readonly Thread _staThread;
    private readonly ManualResetEventSlim _readySignal = new(initialState: false);
    private Form? _hiddenForm;
    private WebView2? _webView;
    private Exception? _initError;
    private volatile bool _disposed;

    /// <summary>
    /// Starts the hidden WebView2 host on a dedicated background thread and
    /// blocks (on the calling thread only — not the new worker thread)
    /// until it's ready to render, or throws if initialization failed (e.g.
    /// the WebView2 Runtime isn't installed).
    /// </summary>
    public PdfRenderer()
    {
        _staThread = new Thread(RunMessageLoop)
        {
            IsBackground = true,
            Name = "UltrasoundApp.Templating.PdfRenderer",
        };
        _staThread.SetApartmentState(ApartmentState.STA);
        _staThread.Start();

        _readySignal.Wait();

        if (_initError is not null)
        {
            throw new InvalidOperationException(
                "Failed to initialize the headless WebView2 host used for PDF rendering. " +
                "Is the WebView2 Runtime installed? (https://developer.microsoft.com/microsoft-edge/webview2/)",
                _initError);
        }
    }

    /// <summary>
    /// Renders <paramref name="html"/> to a PDF file at
    /// <paramref name="outputPdfPath"/> (parent directory created if
    /// needed). Safe to call repeatedly on the same instance; each call
    /// reuses the already-initialized WebView2 environment.
    /// </summary>
    public Task<PdfRenderResult> RenderHtmlToPdfAsync(
        string html,
        string outputPdfPath,
        PdfRenderOptions? options = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(html);
        ArgumentException.ThrowIfNullOrEmpty(outputPdfPath);

        var resolvedOptions = options ?? new PdfRenderOptions();
        var completionSource = new TaskCompletionSource<PdfRenderResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        _hiddenForm!.BeginInvoke((MethodInvoker)(async () =>
        {
            try
            {
                var result = await RenderOnHostThreadAsync(html, outputPdfPath, resolvedOptions);
                completionSource.SetResult(result);
            }
            catch (Exception ex)
            {
                AppLog.Error(LogCategory, $"Failed to render report PDF to '{outputPdfPath}'.", ex);
                completionSource.SetResult(new PdfRenderResult(
                    false,
                    null,
                    "The report PDF could not be created. See data/logs/ for details."));
            }
        }));

        return completionSource.Task;
    }

    /// <summary>Runs entirely on the dedicated STA thread — must not be called from any other thread.</summary>
    private async Task<PdfRenderResult> RenderOnHostThreadAsync(string html, string outputPdfPath, PdfRenderOptions options)
    {
        var core = _webView!.CoreWebView2 ?? throw new InvalidOperationException("WebView2 core is not ready.");

        var navigationCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (e.IsSuccess)
            {
                navigationCompleted.TrySetResult();
            }
            else
            {
                navigationCompleted.TrySetException(
                    new InvalidOperationException($"WebView2 failed to load the report HTML: {e.WebErrorStatus}."));
            }
        }

        core.NavigationCompleted += OnNavigationCompleted;
        try
        {
            // NavigateToString avoids writing a temp .html file to disk just
            // to load it. It's fine for report-sized HTML (a few hundred KB
            // at most); WebView2's documented ceiling for this API is far
            // larger than any single report template will ever be.
            core.NavigateToString(html);
            await navigationCompleted.Task;

            var printSettings = core.Environment.CreatePrintSettings();
            printSettings.ShouldPrintBackgrounds = options.PrintBackgrounds;
            printSettings.ShouldPrintHeaderAndFooter = false;
            printSettings.MarginTop = options.MarginInches;
            printSettings.MarginBottom = options.MarginInches;
            printSettings.MarginLeft = options.MarginInches;
            printSettings.MarginRight = options.MarginInches;
            printSettings.PageWidth = options.PageWidthInches;
            printSettings.PageHeight = options.PageHeightInches;
            printSettings.Orientation = options.LandscapeOrientation
                ? CoreWebView2PrintOrientation.Landscape
                : CoreWebView2PrintOrientation.Portrait;

            var outputDirectory = Path.GetDirectoryName(outputPdfPath);
            if (!string.IsNullOrEmpty(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            var succeeded = await core.PrintToPdfAsync(outputPdfPath, printSettings);

            if (succeeded)
            {
                AppLog.Info(LogCategory, $"Rendered report PDF to '{outputPdfPath}'.");
                return new PdfRenderResult(true, outputPdfPath, $"PDF rendered successfully to '{outputPdfPath}'.");
            }

            AppLog.Error(LogCategory, $"WebView2 PrintToPdfAsync returned false for '{outputPdfPath}'.");
            return new PdfRenderResult(
                false,
                null,
                "The report PDF could not be written. Check that the output folder is writable and has free space.");
        }
        finally
        {
            core.NavigationCompleted -= OnNavigationCompleted;
        }
    }

    /// <summary>
    /// Explicit <see cref="IReportPdfRenderer"/> implementation — delegates
    /// to <see cref="RenderHtmlToPdfAsync(string, string, PdfRenderOptions?)"/>
    /// with default options and maps the result to Core's
    /// <see cref="ReportPdfRenderResult"/> so that Core-level callers
    /// (<c>ReportRenderService</c>) never need to know about
    /// <see cref="PdfRenderResult"/>.
    /// </summary>
    async Task<ReportPdfRenderResult> IReportPdfRenderer.RenderHtmlToPdfAsync(string html, string outputPdfPath)
    {
        PdfRenderResult result = await RenderHtmlToPdfAsync(html, outputPdfPath);
        return new ReportPdfRenderResult(result.Succeeded, result.OutputPdfPath, result.Message);
    }

    /// <summary>Entry point for the dedicated STA thread. Owns the hidden Form's message loop for the lifetime of this <see cref="PdfRenderer"/>.</summary>
    private void RunMessageLoop()
    {
        _hiddenForm = new Form
        {
            // Never actually visible to the user: off-screen position,
            // 1x1 size, minimized, no taskbar entry, fully transparent.
            // It still needs to be "shown" (see Application.Run below) so
            // its window handle is created and its Load event fires, which
            // is where WebView2 initialization happens.
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000),
            Size = new Size(1, 1),
            WindowState = FormWindowState.Minimized,
            Opacity = 0,
        };

        _webView = new WebView2 { Dock = DockStyle.Fill };
        _hiddenForm.Controls.Add(_webView);

        _hiddenForm.Load += async (_, _) =>
        {
            try
            {
                var userDataFolder = TemplatingPaths.GetWebView2UserDataDirectory();
                var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
                await _webView.EnsureCoreWebView2Async(environment);
                _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            }
            catch (Exception ex)
            {
                _initError = ex;
            }
            finally
            {
                _readySignal.Set();
            }
        };

        // Application.Run(Form) shows the form (triggering Load, above)
        // and pumps Win32 messages on this thread until the form closes.
        Application.Run(_hiddenForm);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            var form = _hiddenForm;
            if (form is { IsDisposed: false })
            {
                form.Invoke(() =>
                {
                    _webView?.Dispose();
                    form.Close(); // Application.Run(_hiddenForm) returns once this happens.
                });
            }
        }
        catch
        {
            // Best-effort cleanup — the worker thread may already be gone.
        }

        _staThread.Join(TimeSpan.FromSeconds(5));
        _readySignal.Dispose();
    }
}
