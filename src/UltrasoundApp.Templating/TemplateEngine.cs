using System.Text;
using System.Text.RegularExpressions;
using Scriban;
using Scriban.Runtime;
using UltrasoundApp.Core.Logging;
using UltrasoundApp.Core.Reporting;

namespace UltrasoundApp.Templating;

/// <summary>
/// Outcome of merging field values into a template.
/// </summary>
/// <param name="MergedHtml">The template with every recognized <c>{{TOKEN}}</c> placeholder substituted.</param>
/// <param name="MissingFields">
/// Placeholder tokens that appear in the template but had no matching key in
/// the supplied field-value dictionary. These render as an empty string in
/// <see cref="MergedHtml"/> rather than being left as a literal
/// <c>{{TOKEN}}</c> or throwing — callers can use this list to flag an
/// incomplete report before printing it.
/// </param>
/// <param name="UnusedFields">
/// Keys in the supplied field-value dictionary that didn't match any
/// placeholder actually present in the template. Not an error (a caller may
/// legitimately pass a superset of fields for a generic exam type), but
/// useful for catching typos in field names during development.
/// </param>
public sealed record TemplateMergeResult(
    string MergedHtml,
    IReadOnlyList<string> MissingFields,
    IReadOnlyList<string> UnusedFields);

/// <summary>
/// Merges an HTML report template with a dictionary of field values.
///
/// <para>
/// Templates use bare, ALL_CAPS placeholder tokens — <c>{{PATIENT_NAME}}</c>,
/// <c>{{LIVER_SIZE}}</c>, etc. Under the hood this is valid Scriban syntax
/// (an ALL_CAPS token is just a variable reference), so no custom parser is
/// needed, but callers of this class never need to know or care that
/// Scriban is involved — they just supply a template file/string and a
/// <c>Dictionary&lt;string, string&gt;</c>.
/// </para>
///
/// <para>
/// Field values are substituted verbatim (Scriban does not HTML-encode
/// output by default), so Bangla text and any other Unicode content passes
/// through unchanged.
/// </para>
/// </summary>
public sealed class TemplateEngine : IReportTemplateEngine
{
    private const string LogCategory = "TEMPLATE";

    // Matches {{TOKEN}} where TOKEN is an ALL_CAPS/digits/underscore
    // identifier starting with a letter — i.e. exactly the placeholder
    // style these report templates use. Intentionally does not match
    // lowercase or mixed-case Scriban expressions, so a template is free to
    // use more advanced Scriban features later without this scan
    // misinterpreting them as simple placeholders.
    private static readonly Regex PlaceholderTokenPattern =
        new(@"\{\{\s*([A-Z][A-Z0-9_]*)\s*\}\}", RegexOptions.Compiled);

    /// <summary>
    /// Reads the HTML template file at <paramref name="templateFilePath"/>
    /// and merges it with <paramref name="fieldValues"/>. See
    /// <see cref="MergeFromString"/> for the merge semantics.
    /// </summary>
    public TemplateMergeResult Merge(string templateFilePath, IReadOnlyDictionary<string, string> fieldValues)
    {
        if (!File.Exists(templateFilePath))
        {
            AppLog.Error(LogCategory, $"Report template file not found: '{templateFilePath}'.");
            throw new TemplateRenderException(
                $"The report template '{Path.GetFileName(templateFilePath)}' could not be found. " +
                "Check the Report Templates Folder setting, or that the template file still exists.");
        }

        string templateHtml;
        try
        {
            templateHtml = File.ReadAllText(templateFilePath, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            AppLog.Error(LogCategory, $"Failed to read report template '{templateFilePath}'.", ex);
            throw new TemplateRenderException(
                $"The report template '{Path.GetFileName(templateFilePath)}' could not be read. " +
                "It may be open in another program, or you may not have permission to read it.", ex);
        }

        TemplateMergeResult result = MergeFromString(templateHtml, fieldValues);

        if (result.MissingFields.Count > 0)
        {
            // Not an error: a missing field renders blank by design (see
            // MergeFromString). Logged as a warning because a blank section
            // on a printed clinical report is exactly the kind of thing
            // someone will later need to explain.
            AppLog.Warning(
                LogCategory,
                $"Template '{Path.GetFileName(templateFilePath)}' rendered with {result.MissingFields.Count} " +
                $"field(s) left blank (no value supplied): {string.Join(", ", result.MissingFields)}");
        }

        return result;
    }

    /// <summary>
    /// Merges an in-memory HTML template string with
    /// <paramref name="fieldValues"/>.
    ///
    /// <list type="bullet">
    ///   <item>Every <c>{{TOKEN}}</c> placeholder found in the template is
    ///   substituted with the matching dictionary value, or an empty string
    ///   if the dictionary has no entry for that token (see
    ///   <see cref="TemplateMergeResult.MissingFields"/>).</item>
    ///   <item>Dictionary keys that don't correspond to any placeholder in
    ///   the template are simply ignored for rendering purposes, but
    ///   reported back via <see cref="TemplateMergeResult.UnusedFields"/>.</item>
    /// </list>
    /// </summary>
    public TemplateMergeResult MergeFromString(string templateHtml, IReadOnlyDictionary<string, string> fieldValues)
    {
        ArgumentNullException.ThrowIfNull(templateHtml);
        ArgumentNullException.ThrowIfNull(fieldValues);

        var tokensInTemplate = PlaceholderTokenPattern
            .Matches(templateHtml)
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();

        var missingFields = tokensInTemplate
            .Where(token => !fieldValues.ContainsKey(token))
            .OrderBy(token => token, StringComparer.Ordinal)
            .ToList();

        var unusedFields = fieldValues.Keys
            .Where(key => !tokensInTemplate.Contains(key))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

        var scriptObject = new ScriptObject();

        // Seed every token the template actually references with an empty
        // string first, so a field the caller didn't supply renders as
        // blank instead of Scriban throwing on an undefined variable or
        // leaving the literal "{{TOKEN}}" text in the output.
        foreach (var token in tokensInTemplate)
        {
            scriptObject[token] = string.Empty;
        }

        foreach (var (key, value) in fieldValues)
        {
            scriptObject[key] = value ?? string.Empty;
        }

        var template = Template.Parse(templateHtml);
        if (template.HasErrors)
        {
            var errors = string.Join("; ", template.Messages.Select(m => m.ToString()));
            AppLog.Error(LogCategory, $"Report template failed to parse: {errors}");
            throw new TemplateRenderException(
                "The report template has a syntax error and could not be used. " +
                "It may have been edited incorrectly — see data/logs/ for the exact location.");
        }

        var context = new TemplateContext();
        context.PushGlobal(scriptObject);

        string mergedHtml;
        try
        {
            mergedHtml = template.Render(context);
        }
        catch (Exception ex)
        {
            AppLog.Error(LogCategory, "Report template failed to render.", ex);
            throw new TemplateRenderException(
                "The report template could not be filled in. " +
                "It may use a feature this app doesn't support — see data/logs/ for details.", ex);
        }

        return new TemplateMergeResult(mergedHtml, missingFields, unusedFields);
    }

    /// <summary>
    /// Explicit <see cref="IReportTemplateEngine"/> implementation — delegates
    /// to <see cref="Merge(string, IReadOnlyDictionary{string, string})"/>
    /// and maps the result to Core's <see cref="ReportMergeResult"/> so that
    /// Core-level callers (<c>ReportRenderService</c>) never need to know
    /// about <see cref="TemplateMergeResult"/>.
    /// </summary>
    ReportMergeResult IReportTemplateEngine.Merge(string templateFilePath, IReadOnlyDictionary<string, string> fieldValues)
    {
        TemplateMergeResult result = Merge(templateFilePath, fieldValues);
        return new ReportMergeResult(result.MergedHtml, result.MissingFields, result.UnusedFields);
    }
}
