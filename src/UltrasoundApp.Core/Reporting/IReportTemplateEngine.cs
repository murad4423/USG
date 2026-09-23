namespace UltrasoundApp.Core.Reporting;

/// <summary>
/// Outcome of merging field values into a report template.
///
/// Mirrors <c>UltrasoundApp.Templating.TemplateMergeResult</c> field-for-field,
/// but is defined here in Core so that Core-level services (see
/// <see cref="Services.ReportRenderService"/>) can depend on the merge
/// operation through <see cref="IReportTemplateEngine"/> without Core having
/// to reference the Templating project — the same reasoning documented on
/// <see cref="Repositories.IPatientRepository"/> for why repository
/// interfaces live in Core while their implementations live in the
/// project that actually needs the extra dependency (Data for
/// repositories, Templating for this one).
/// </summary>
public sealed record ReportMergeResult(
    string MergedHtml,
    IReadOnlyList<string> MissingFields,
    IReadOnlyList<string> UnusedFields);

/// <summary>
/// Abstraction over merging an HTML report template file with a dictionary
/// of field values. Implemented by <c>UltrasoundApp.Templating.TemplateEngine</c>.
/// </summary>
public interface IReportTemplateEngine
{
    /// <summary>
    /// Reads the HTML template file at <paramref name="templateFilePath"/>
    /// and merges it with <paramref name="fieldValues"/>.
    /// </summary>
    ReportMergeResult Merge(string templateFilePath, IReadOnlyDictionary<string, string> fieldValues);
}
