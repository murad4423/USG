namespace UltrasoundApp.Core.Reporting;

/// <summary>Outcome of rendering merged report HTML to a PDF file.</summary>
public sealed record ReportPdfRenderResult(bool Succeeded, string? OutputPdfPath, string Message);

/// <summary>
/// Abstraction over converting merged report HTML into a PDF file on disk.
/// Implemented by <c>UltrasoundApp.Templating.PdfRenderer</c> (WebView2
/// headless print-to-PDF). See <see cref="IReportTemplateEngine"/> for why
/// this interface lives in Core instead of Templating.
/// </summary>
public interface IReportPdfRenderer
{
    /// <summary>Renders <paramref name="html"/> to a PDF file at <paramref name="outputPdfPath"/> using default page/margin options.</summary>
    Task<ReportPdfRenderResult> RenderHtmlToPdfAsync(string html, string outputPdfPath);
}
