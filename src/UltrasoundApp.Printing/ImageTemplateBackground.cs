using UltrasoundApp.Core.Logging;

namespace UltrasoundApp.Printing;

/// <summary>
/// Turns the Settings screen's uploaded "Image Template" PDF (see
/// <c>WebViewBridge.HandleUploadImageTemplate</c>) into a background image
/// that can be (a) shown instantly behind the client-side live preview
/// (<c>LiveImageSheetPreview.tsx</c>) and (b) drawn as the actual page
/// background of the printed/composed sheet (<see cref="ImageSheetComposer"/>).
///
/// <para>
/// <b>Why rasterize once and cache, instead of rendering the PDF every
/// time:</b> the operator asked for this to feel real-time and never slow
/// down selecting/deselecting images or printing. Rasterizing a PDF page is
/// the one genuinely slow step in this whole feature (tens to low-hundreds
/// of milliseconds), so it must never run on the "select an image" or
/// "print" hot path. Instead it runs exactly once per uploaded template —
/// right after upload (see <c>WebViewBridge.HandleUploadImageTemplate</c>)
/// — and the result is cached to disk as a PNG next to the PDF.
/// <see cref="EnsureBackgroundImage"/> is safe to call on every compose/
/// print anyway (it's what both the preview and print code paths do): it
/// only re-rasterizes if the cached PNG is missing or older than the PDF
/// (e.g. the app starts up with a template uploaded in a previous session),
/// otherwise it's just two file-timestamp checks.
/// </para>
///
/// <para>
/// <b>Why PDFium (via the PDFtoImage package) instead of, say, WebView2:</b>
/// this needs an exact, high-fidelity raster of the operator's own PDF
/// (their hospital's letterhead/design) at a fixed pixel size matching the
/// print page — a dedicated PDF rasterizer gives a deterministic result
/// with no browser chrome/scrollbars/zoom quirks to fight, and no new
/// window/thread to manage (unlike <c>UltrasoundApp.Templating.PdfRenderer</c>,
/// which hosts a hidden WebView2 for HTML-&gt;PDF, the opposite direction).
/// </para>
/// </summary>
public static class ImageTemplateBackground
{
    private const string LogCategory = "PRINT";

    /// <summary>File name the cached background PNG is stored under, alongside the uploaded PDF.</summary>
    private const string BackgroundFileName = "background-cache.png";

    /// <summary>
    /// DPI used to rasterize the template PDF's first page. ~200 DPI is
    /// sharp on a laser-printed A4/Letter page without producing an
    /// unreasonably large PNG (a few MB at most) or a slow render.
    /// </summary>
    private const int RenderDpi = 200;

    /// <summary>
    /// Full path to the cached background PNG for a given template PDF path
    /// (same folder, fixed file name — there is only ever one template at a
    /// time, same as the PDF itself). Does not check that either file
    /// exists.
    /// </summary>
    public static string GetCachePathFor(string templatePdfPath)
    {
        string directory = Path.GetDirectoryName(templatePdfPath)
            ?? throw new ArgumentException("Template path has no directory.", nameof(templatePdfPath));
        return Path.Combine(directory, BackgroundFileName);
    }

    /// <summary>
    /// Ensures the cached background PNG for <paramref name="templatePdfPath"/>
    /// exists and is up to date, (re)rendering it from the PDF's first page
    /// if needed. Returns the PNG's path, or <see langword="null"/> if there
    /// is no template (<paramref name="templatePdfPath"/> is null/missing)
    /// or rendering failed (e.g. a corrupt PDF) — either way the caller
    /// (<see cref="ImageSheetComposer"/> / the live-preview IPC handlers)
    /// simply falls back to no background, the same as if no template had
    /// been uploaded.
    /// </summary>
    public static string? EnsureBackgroundImage(string? templatePdfPath)
    {
        if (string.IsNullOrWhiteSpace(templatePdfPath) || !File.Exists(templatePdfPath))
        {
            return null;
        }

        string cachePath = GetCachePathFor(templatePdfPath);

        try
        {
            if (File.Exists(cachePath) &&
                File.GetLastWriteTimeUtc(cachePath) >= File.GetLastWriteTimeUtc(templatePdfPath))
            {
                return cachePath;
            }

            using (var pdfStream = File.OpenRead(templatePdfPath))
            {
                PDFtoImage.Conversion.SavePng(
                    imageFilename: cachePath,
                    pdfStream: pdfStream,
                    page: 0,
                    options: new PDFtoImage.RenderOptions(Dpi: RenderDpi));
            }

            // So the "is the cache newer than the PDF" check above works
            // reliably even on filesystems with coarse write-time
            // resolution — and so InvalidateAsync-style re-uploads (a new
            // PDF landing with an earlier mtime than the old cache, in
            // theory) still trigger a re-render next time.
            File.SetLastWriteTimeUtc(cachePath, DateTime.UtcNow);

            AppLog.Info(LogCategory, $"Rendered image-template background '{cachePath}' from '{templatePdfPath}'.");
            return cachePath;
        }
        catch (Exception ex)
        {
            AppLog.Error(LogCategory, $"Failed to rasterize the image template '{templatePdfPath}' to a background image.", ex);
            return null;
        }
    }

    /// <summary>
    /// Deletes the cached background PNG (if any) next to
    /// <paramref name="templatePdfPath"/>. Called when the template is
    /// replaced with a new PDF, before regenerating — avoids ever serving a
    /// stale background if regeneration itself fails.
    /// </summary>
    public static void InvalidateCache(string templatePdfPath)
    {
        try
        {
            string cachePath = GetCachePathFor(templatePdfPath);
            if (File.Exists(cachePath))
            {
                File.Delete(cachePath);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error(LogCategory, $"Failed to remove the stale image-template background cache for '{templatePdfPath}'.", ex);
        }
    }
}
