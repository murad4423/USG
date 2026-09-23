using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Printing;
using UltrasoundApp.Core.Logging;

namespace UltrasoundApp.Printing;

/// <summary>Which code path a <see cref="SilentPrinter"/> call actually took.</summary>
public enum SilentPrintMode
{
    /// <summary>Sent to a configured command-line silent-print helper (e.g. SumatraPDF-style <c>-print-to -silent</c>). Genuinely dialog-free.</summary>
    SilentlyPrintedViaHelper,

    /// <summary>
    /// Every page of the PDF was rendered with PDFium (the PDFtoImage package this project already uses) and sent straight to the
    /// printer through Windows' own print API (<see cref="PrintDocument"/>). Needs no PDF viewer, no helper program and shows no dialog.
    /// </summary>
    PrintedDirectly,

    /// <summary>Sent via the PDF file's registered handler using the "printto" shell verb. Best-effort — see <see cref="SilentPrinter"/> remarks.</summary>
    SilentlyPrintedViaShellVerb,

    /// <summary>No usable printer, or both print attempts failed — the PDF was copied to disk (and an attempt made to open it) instead.</summary>
    SavedToDiskFallback,
}

/// <summary>Outcome of a <see cref="SilentPrinter.Print"/> call.</summary>
public sealed record SilentPrintResult(
    SilentPrintMode Mode,
    bool Succeeded,
    string? PrinterUsed,
    string? OutputPath,
    string Message);

/// <summary>
/// Sends an already-generated PDF to a printer without showing the OS print
/// dialog.
///
/// <para>
/// <b>Read this before assuming "silent" means bulletproof.</b> .NET has no
/// built-in API that rasterizes and prints an arbitrary PDF's pages —
/// <c>System.Drawing.Printing</c> draws GDI+ graphics, it does not parse PDF
/// content streams. Genuinely reliable, always-dialog-free PDF printing
/// normally means shelling out to a small tool built for exactly that (e.g.
/// SumatraPDF's <c>-print-to "&lt;printer&gt;" -silent "&lt;file&gt;"</c>
/// flags, or a PDF SDK such as PDFium/Ghostscript). This class is written to
/// use such a helper if one is configured via
/// <see cref="SilentPrintHelperExecutablePath"/>, and degrades through
/// progressively weaker paths if not — see <see cref="Print"/>.
/// </para>
/// </summary>
public sealed class SilentPrinter
{
    private const string LogCategory = "PRINT";

    /// <summary>
    /// Optional path to a command-line PDF-printing helper that accepts
    /// SumatraPDF-style arguments (<c>-print-to "&lt;printer&gt;" -silent
    /// "&lt;file&gt;"</c>). Left <c>null</c> by default since no such tool is
    /// bundled with this project yet — set this once one is added to unlock
    /// the fully-silent code path instead of the shell-verb best effort.
    /// </summary>
    public string? SilentPrintHelperExecutablePath { get; init; }

    /// <summary>
    /// Resolution each PDF page is rendered at before being sent to the
    /// printer by <see cref="TryPrintDirectly"/>. 300 DPI is sharp for
    /// ultrasound photos on an office/inkjet printer without making the
    /// spool job huge.
    /// </summary>
    private const int DirectPrintDpi = 300;

    /// <summary>
    /// Sends <paramref name="pdfFilePath"/> to <paramref name="printerName"/>
    /// (or the system default printer if omitted), attempting the most
    /// reliable available path in order:
    /// <list type="number">
    ///   <item>A configured silent-print helper executable, if set and present on disk.</item>
    ///   <item>Direct print: every page is rendered with PDFium and sent to the printer through Windows' own print API (<see cref="TryPrintDirectly"/>) — no PDF viewer or helper needed, no dialog.</item>
    ///   <item>The PDF's registered handler via the Windows "printto" shell verb (best-effort).</item>
    ///   <item>If no printer is installed at all, or all attempts above fail: copy the PDF into <c>data/print-fallback/</c> and try to open it with the default viewer, so it can be inspected/printed manually. This is the expected path on a dev machine with no printers configured.</item>
    /// </list>
    /// </summary>
    public SilentPrintResult Print(string pdfFilePath, string? printerName = null)
    {
        if (!File.Exists(pdfFilePath))
        {
            AppLog.Error(LogCategory, $"PDF to print does not exist: '{pdfFilePath}'.");
            throw new FileNotFoundException("PDF file to print was not found.", pdfFilePath);
        }

        var resolvedPrinter = ResolvePrinterName(printerName);

        // The printer-not-found case specifically: the operator picked a
        // printer in Settings, but Windows no longer reports it (unplugged,
        // driver removed, renamed). Worth a WARNING of its own because the
        // print may still "succeed" on a different printer below, which is
        // otherwise a confusing silent substitution.
        if (!string.IsNullOrWhiteSpace(printerName) &&
            !string.Equals(resolvedPrinter, printerName, StringComparison.OrdinalIgnoreCase))
        {
            AppLog.Warning(
                LogCategory,
                $"Requested printer '{printerName}' is not installed. " +
                $"Falling back to {(resolvedPrinter is null ? "save-to-disk (no printers installed)" : $"'{resolvedPrinter}'")}.");
        }

        string? directPrintFailure = null;

        if (resolvedPrinter is not null)
        {
            if (!string.IsNullOrWhiteSpace(SilentPrintHelperExecutablePath) && File.Exists(SilentPrintHelperExecutablePath))
            {
                var helperResult = TryPrintViaHelper(pdfFilePath, resolvedPrinter);
                if (helperResult.Succeeded)
                {
                    AppLog.Info(LogCategory, $"Printed '{Path.GetFileName(pdfFilePath)}' to '{resolvedPrinter}' via the silent-print helper.");
                    return helperResult;
                }

                AppLog.Warning(LogCategory, $"Silent-print helper failed for '{resolvedPrinter}': {helperResult.Message}");
            }

            // Reliable path that needs nothing installed: render the pages
            // ourselves and hand them to the Windows print spooler.
            var directResult = TryPrintDirectly(pdfFilePath, resolvedPrinter);
            if (directResult.Succeeded)
            {
                AppLog.Info(LogCategory, $"Printed '{Path.GetFileName(pdfFilePath)}' to '{resolvedPrinter}' directly (PDFium render + Windows print API).");
                return directResult;
            }

            directPrintFailure = directResult.Message;
            AppLog.Warning(LogCategory, $"Direct print failed for '{resolvedPrinter}': {directResult.Message}");

            var shellResult = TryPrintViaShellVerb(pdfFilePath, resolvedPrinter);
            if (shellResult.Succeeded)
            {
                AppLog.Info(LogCategory, $"Printed '{Path.GetFileName(pdfFilePath)}' to '{resolvedPrinter}' via the printto shell verb.");
                return shellResult;
            }

            AppLog.Warning(LogCategory, $"Shell 'printto' failed for '{resolvedPrinter}': {shellResult.Message}");
        }

        var fallback = SaveToDiskFallback(pdfFilePath, resolvedPrinter, directPrintFailure);
        if (fallback.Succeeded)
        {
            AppLog.Warning(
                LogCategory,
                $"Could not print '{Path.GetFileName(pdfFilePath)}' to a printer — saved to '{fallback.OutputPath}' instead.");
        }
        else
        {
            AppLog.Error(
                LogCategory,
                $"Printing '{Path.GetFileName(pdfFilePath)}' failed entirely, including the save-to-disk fallback: {fallback.Message}");
        }

        return fallback;
    }

    /// <summary>
    /// Names of every printer Windows currently has installed — for
    /// populating a printer-selection dropdown (the Settings screen).
    /// Returns an empty list rather than throwing on a machine with no
    /// print subsystem configured at all (a minimal dev/CI environment),
    /// the same defensive handling <see cref="ResolvePrinterName"/>
    /// already does internally for an actual print call.
    /// </summary>
    public static IReadOnlyList<string> GetInstalledPrinterNames()
    {
        try
        {
            return PrinterSettings.InstalledPrinters.Cast<string>().ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static string? ResolvePrinterName(string? requestedPrinterName)
    {
        List<string> installed;
        try
        {
            installed = PrinterSettings.InstalledPrinters.Cast<string>().ToList();
        }
        catch
        {
            // Some machines (notably minimal dev/CI environments) don't have
            // a print subsystem configured at all — treat that the same as
            // "no printers" rather than letting it bubble up.
            return null;
        }

        if (installed.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(requestedPrinterName) &&
            installed.Any(p => string.Equals(p, requestedPrinterName, StringComparison.OrdinalIgnoreCase)))
        {
            return requestedPrinterName;
        }

        var systemDefault = new PrinterSettings().PrinterName;
        if (!string.IsNullOrWhiteSpace(systemDefault) &&
            installed.Any(p => string.Equals(p, systemDefault, StringComparison.OrdinalIgnoreCase)))
        {
            return systemDefault;
        }

        return installed[0];
    }

    /// <summary>
    /// Prints the PDF without any PDF viewer: each page is rasterized with
    /// PDFium (PDFtoImage) and drawn onto a <see cref="PrintDocument"/> for
    /// the chosen printer. <see cref="StandardPrintController"/> suppresses
    /// the "Printing page x of y" progress window, so nothing is shown.
    /// Each page is scaled (aspect ratio kept) to fit the printer's
    /// printable area, so the template's header/footer are never clipped by
    /// the printer's unprintable edge.
    /// </summary>
    private static SilentPrintResult TryPrintDirectly(string pdfFilePath, string printerName)
    {
        string? tempDirectory = null;

        try
        {
            int pageCount;
            using (var countStream = File.OpenRead(pdfFilePath))
            {
                pageCount = PDFtoImage.Conversion.GetPageCount(countStream);
            }

            if (pageCount <= 0)
            {
                return DirectFailure(printerName, pdfFilePath, "the PDF has no pages.");
            }

            tempDirectory = Path.Combine(Path.GetTempPath(), $"usg-print-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDirectory);

            var pagePaths = new List<string>(pageCount);
            for (var i = 0; i < pageCount; i++)
            {
                var pagePath = Path.Combine(tempDirectory, $"page-{i + 1}.png");

                // SavePng closes the stream it is given, so every page gets its own.
                using (var pageStream = File.OpenRead(pdfFilePath))
                {
                    PDFtoImage.Conversion.SavePng(
                        imageFilename: pagePath,
                        pdfStream: pageStream,
                        page: i,
                        options: new PDFtoImage.RenderOptions(Dpi: DirectPrintDpi));
                }

                pagePaths.Add(pagePath);
            }

            using var document = new PrintDocument();
            document.PrinterSettings.PrinterName = printerName;

            if (!document.PrinterSettings.IsValid)
            {
                return DirectFailure(printerName, pdfFilePath, $"Windows reports '{printerName}' as not a valid printer.");
            }

            document.DocumentName = Path.GetFileName(pdfFilePath);
            document.PrintController = new StandardPrintController();
            document.OriginAtMargins = false;
            document.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);

            using (var firstPage = Image.FromFile(pagePaths[0]))
            {
                document.DefaultPageSettings.Landscape = firstPage.Width > firstPage.Height;
                TrySelectPaperSize(document, firstPage.Width, firstPage.Height);
            }

            var pageIndex = 0;
            document.PrintPage += (_, e) =>
            {
                var graphics = e.Graphics ?? throw new InvalidOperationException("The printer gave no drawing surface.");

                using var image = Image.FromFile(pagePaths[pageIndex]);

                // Graphics origin = top-left of the printable area, in 1/100 inch.
                var printable = e.PageSettings.PrintableArea;
                var areaWidth = printable.Width > 0 ? printable.Width : e.PageBounds.Width;
                var areaHeight = printable.Height > 0 ? printable.Height : e.PageBounds.Height;

                var scale = Math.Min(areaWidth / image.Width, areaHeight / image.Height);
                var drawWidth = image.Width * scale;
                var drawHeight = image.Height * scale;
                var drawX = (areaWidth - drawWidth) / 2f;
                var drawY = (areaHeight - drawHeight) / 2f;

                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.SmoothingMode = SmoothingMode.HighQuality;
                graphics.DrawImage(image, drawX, drawY, drawWidth, drawHeight);

                pageIndex++;
                e.HasMorePages = pageIndex < pagePaths.Count;
            };

            document.Print();

            return new SilentPrintResult(
                SilentPrintMode.PrintedDirectly,
                true,
                printerName,
                pdfFilePath,
                pageCount == 1
                    ? $"Sent 1 page to '{printerName}'."
                    : $"Sent {pageCount} pages to '{printerName}'.");
        }
        catch (Exception ex)
        {
            return DirectFailure(printerName, pdfFilePath, ex.Message);
        }
        finally
        {
            if (tempDirectory is not null)
            {
                try
                {
                    Directory.Delete(tempDirectory, recursive: true);
                }
                catch
                {
                    // Temp files only — the OS clears them eventually.
                }
            }
        }
    }

    private static SilentPrintResult DirectFailure(string printerName, string pdfFilePath, string reason) =>
        new(SilentPrintMode.PrintedDirectly, false, printerName, pdfFilePath, $"Direct print failed: {reason}");

    /// <summary>
    /// Points the print job at the printer's A4 (or Letter) paper size when
    /// the PDF page is that shape, so the printer driver does not silently
    /// pick a different default paper. Best-effort: any problem just leaves
    /// the printer's own default.
    /// </summary>
    private static void TrySelectPaperSize(PrintDocument document, int pixelWidth, int pixelHeight)
    {
        try
        {
            var aspect = (double)Math.Min(pixelWidth, pixelHeight) / Math.Max(pixelWidth, pixelHeight);

            PaperKind? wanted = null;
            if (Math.Abs(aspect - 210.0 / 297.0) < 0.01)
            {
                wanted = PaperKind.A4;
            }
            else if (Math.Abs(aspect - 8.5 / 11.0) < 0.01)
            {
                wanted = PaperKind.Letter;
            }

            if (wanted is null)
            {
                return;
            }

            foreach (PaperSize size in document.PrinterSettings.PaperSizes)
            {
                if (size.Kind == wanted)
                {
                    document.DefaultPageSettings.PaperSize = size;
                    return;
                }
            }
        }
        catch
        {
            // Keep the printer's default paper.
        }
    }

    private SilentPrintResult TryPrintViaHelper(string pdfFilePath, string printerName)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = SilentPrintHelperExecutablePath,
                Arguments = $"-print-to \"{printerName}\" -silent \"{pdfFilePath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            using var process = Process.Start(startInfo);
            var exited = process is not null && process.WaitForExit(30_000);
            var succeeded = exited && process!.ExitCode == 0;

            return new SilentPrintResult(
                SilentPrintMode.SilentlyPrintedViaHelper,
                succeeded,
                printerName,
                pdfFilePath,
                succeeded
                    ? $"Sent to '{printerName}' via the silent-print helper — no dialog shown."
                    : "Silent-print helper did not report success; falling back.");
        }
        catch (Exception ex)
        {
            return new SilentPrintResult(
                SilentPrintMode.SilentlyPrintedViaHelper,
                false,
                printerName,
                pdfFilePath,
                $"Silent-print helper failed to run: {ex.Message}");
        }
    }

    private SilentPrintResult TryPrintViaShellVerb(string pdfFilePath, string printerName)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = pdfFilePath,
                Verb = "printto",
                Arguments = $"\"{printerName}\"",
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            using var process = Process.Start(startInfo);
            process?.WaitForExit(30_000);

            return new SilentPrintResult(
                SilentPrintMode.SilentlyPrintedViaShellVerb,
                true,
                printerName,
                pdfFilePath,
                $"Sent to '{printerName}' via the PDF file's default handler (\"printto\" verb). " +
                "Best-effort: whether this stays fully silent depends on that handler (e.g. Adobe " +
                "Acrobat/Reader honors it well; some browser-based PDF viewers may still flash a window).");
        }
        catch (Exception ex)
        {
            return new SilentPrintResult(
                SilentPrintMode.SilentlyPrintedViaShellVerb,
                false,
                printerName,
                pdfFilePath,
                $"\"printto\" attempt failed (often means no PDF handler is registered for this verb): {ex.Message}");
        }
    }

    private static SilentPrintResult SaveToDiskFallback(string pdfFilePath, string? attemptedPrinter, string? failureReason = null)
    {
        var fallbackDirectory = PrintingPaths.GetPrintFallbackDirectory();
        var destination = Path.Combine(
            fallbackDirectory,
            $"{Path.GetFileNameWithoutExtension(pdfFilePath)}_{DateTime.Now:yyyyMMdd_HHmmss}.pdf");

        File.Copy(pdfFilePath, destination, overwrite: true);

        var message = attemptedPrinter is null
            ? "No installed printer was found — saved the PDF to disk instead."
            : $"Could not print to '{attemptedPrinter}' — saved the PDF to disk instead.";

        if (!string.IsNullOrWhiteSpace(failureReason))
        {
            message += $" ({failureReason})";
        }

        try
        {
            Process.Start(new ProcessStartInfo(destination) { UseShellExecute = true });
            message += " Opened it in the default viewer for manual inspection.";
        }
        catch (Exception ex)
        {
            message += $" Could not auto-open it ({ex.Message}) — open it manually to inspect.";
        }

        return new SilentPrintResult(SilentPrintMode.SavedToDiskFallback, true, attemptedPrinter, destination, message);
    }
}
