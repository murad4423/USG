namespace UltrasoundApp.Templating;

/// <summary>
/// Thrown by <see cref="TemplateEngine"/> when a report template can't be
/// found, read, parsed or rendered, and by report PDF generation when the
/// merged HTML can't be turned into a PDF.
///
/// <para>
/// As with <c>UltrasoundApp.Dicom.DicomParseException</c>, the
/// <see cref="Exception.Message"/> is written for a sonographer and is safe
/// to show directly in the UI — it never contains Scriban parser output, a
/// file path from the developer's machine, or a stack trace. The technical
/// cause is preserved as <see cref="Exception.InnerException"/> where there
/// is one, and the full detail is already written to <c>data/logs/</c>.
/// </para>
///
/// <para>
/// Note that a <i>missing field value</i> is deliberately NOT an exception:
/// <see cref="TemplateEngine.MergeFromString"/> renders unsupplied
/// placeholders as blank and reports them via
/// <c>TemplateMergeResult.MissingFields</c> (logged as a warning). Only a
/// template that cannot be used at all throws.
/// </para>
/// </summary>
public sealed class TemplateRenderException : Exception
{
    public TemplateRenderException(string message) : base(message)
    {
    }

    public TemplateRenderException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
