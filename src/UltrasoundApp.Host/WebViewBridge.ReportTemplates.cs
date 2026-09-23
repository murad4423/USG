using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using UltrasoundApp.Core.Logging;
using UltrasoundApp.Printing;

namespace UltrasoundApp.Host;

/// <summary>
/// Report Template Builder (Settings &gt; Report Template) persistence.
///
/// Kept in its own file so WebViewBridge.cs only needs a one-line hook for
/// these requests. Each saved template is one small JSON file in
/// <c>data/report-builder-templates/{id}.json</c> (same "plain files under
/// data/" approach as the image-template content area) — so it survives
/// restarts and needs no database change.
///
///   - "getReportBuilderTemplates"   -&gt; every saved template (with its HTML)
///   - "saveReportBuilderTemplate"   -&gt; {"template": {...}} create (no id) or update (id)
///   - "deleteReportBuilderTemplate" -&gt; {"id": "..."}
///
/// Each replies with a single "{requestType}Result" message.
/// </summary>
public sealed partial class WebViewBridge
{
    private const string LogCategoryReportTemplates = "REPORT_TEMPLATES";
    private const int ReportBuilderMaxNameLength = 120;
    private const int ReportBuilderMaxHtmlLength = 2_000_000;
    private const double ReportBuilderMaxMarginInches = 3.0;

    private static readonly JsonSerializerOptions ReportBuilderFileJsonOptions = new()
    {
        WriteIndented = true,
        // Keeps Bengali (and other non-ASCII) text readable in the .json file.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Entry point called from OnWebMessageReceived for the three request types above.</summary>
    private void HandleReportBuilderTemplateRequest(string requestType, string rawMessage)
    {
        string resultType = requestType + "Result";

        try
        {
            ReportBuilderRequest? request = JsonSerializer.Deserialize<ReportBuilderRequest>(rawMessage);

            switch (requestType)
            {
                case "getReportBuilderTemplates":
                    Send(new ReportBuilderResult
                    {
                        Type = resultType,
                        Success = true,
                        Templates = LoadAllReportBuilderTemplates()
                    });
                    break;

                case "saveReportBuilderTemplate":
                    SaveReportBuilderTemplate(resultType, request?.Template);
                    break;

                case "deleteReportBuilderTemplate":
                    DeleteReportBuilderTemplate(resultType, request?.Id);
                    break;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error(LogCategoryReportTemplates, $"Report template request '{requestType}' failed.", ex);
            Send(new ReportBuilderResult
            {
                Type = resultType,
                Success = false,
                Error = "The report template request failed. See data/logs/ for details."
            });
        }
    }

    private void SaveReportBuilderTemplate(string resultType, ReportBuilderTemplateDto? incoming)
    {
        if (incoming is null)
        {
            SendReportBuilderError(resultType, "No template was received.");
            return;
        }

        string name = (incoming.Name ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            SendReportBuilderError(resultType, "Enter a template name before saving.");
            return;
        }

        if (name.Length > ReportBuilderMaxNameLength)
        {
            SendReportBuilderError(resultType, $"The template name is too long (max {ReportBuilderMaxNameLength} characters).");
            return;
        }

        string html = incoming.Html ?? string.Empty;
        if (html.Length > ReportBuilderMaxHtmlLength)
        {
            SendReportBuilderError(resultType, "The template content is too large to save.");
            return;
        }

        string id;
        if (string.IsNullOrWhiteSpace(incoming.Id))
        {
            id = Guid.NewGuid().ToString("N");
        }
        else if (IsValidReportBuilderTemplateId(incoming.Id))
        {
            id = incoming.Id;
        }
        else
        {
            SendReportBuilderError(resultType, "The template id is not valid.");
            return;
        }

        List<ReportBuilderTemplateDto> existing = LoadAllReportBuilderTemplates();

        bool nameTaken = existing.Any(t =>
            !string.Equals(t.Id, id, StringComparison.Ordinal)
            && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        if (nameTaken)
        {
            SendReportBuilderError(resultType, $"A template named \"{name}\" already exists. Choose a different name.");
            return;
        }

        string now = DateTime.UtcNow.ToString("o");
        ReportBuilderTemplateDto? previous = existing.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.Ordinal));

        var toSave = new ReportBuilderTemplateDto
        {
            Id = id,
            Name = name,
            PageSize = incoming.PageSize == "Letter" ? "Letter" : "A4",
            Margins = new ReportBuilderMargins
            {
                Top = ClampReportBuilderMargin(incoming.Margins?.Top),
                Right = ClampReportBuilderMargin(incoming.Margins?.Right),
                Bottom = ClampReportBuilderMargin(incoming.Margins?.Bottom),
                Left = ClampReportBuilderMargin(incoming.Margins?.Left)
            },
            Html = html,
            CreatedAt = previous?.CreatedAt ?? now,
            UpdatedAt = now
        };

        string path = GetReportBuilderTemplatePath(id);
        string tempPath = path + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(toSave, ReportBuilderFileJsonOptions), new UTF8Encoding(false));
        File.Move(tempPath, path, overwrite: true);

        AppLog.Info(LogCategoryReportTemplates, $"Saved report template '{name}' ({id}).");

        Send(new ReportBuilderResult
        {
            Type = resultType,
            Success = true,
            Template = toSave
        });
    }

    private void DeleteReportBuilderTemplate(string resultType, string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || !IsValidReportBuilderTemplateId(id))
        {
            SendReportBuilderError(resultType, "The template id is not valid.");
            return;
        }

        string path = GetReportBuilderTemplatePath(id);
        if (File.Exists(path))
        {
            File.Delete(path);
            AppLog.Info(LogCategoryReportTemplates, $"Deleted report template {id}.");
        }

        Send(new ReportBuilderResult { Type = resultType, Success = true });
    }

    private void SendReportBuilderError(string resultType, string error)
    {
        Send(new ReportBuilderResult { Type = resultType, Success = false, Error = error });
    }

    private static string GetReportBuilderTemplatesDirectory()
    {
        string dir = Path.Combine(PrintingPaths.GetRepoRoot(), "data", "report-builder-templates");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string GetReportBuilderTemplatePath(string id) =>
        Path.Combine(GetReportBuilderTemplatesDirectory(), id + ".json");

    /// <summary>Ids are 32 hex characters (Guid "N" format) — this also guarantees the id can never be used to escape the templates folder.</summary>
    private static bool IsValidReportBuilderTemplateId(string id) =>
        id.Length == 32 && id.All(Uri.IsHexDigit);

    private static double ClampReportBuilderMargin(double? value)
    {
        if (value is null || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
        {
            return 1.0;
        }

        return Math.Round(Math.Clamp(value.Value, 0, ReportBuilderMaxMarginInches), 2);
    }

    private static List<ReportBuilderTemplateDto> LoadAllReportBuilderTemplates()
    {
        var results = new List<ReportBuilderTemplateDto>();

        foreach (string file in Directory.EnumerateFiles(GetReportBuilderTemplatesDirectory(), "*.json"))
        {
            try
            {
                var template = JsonSerializer.Deserialize<ReportBuilderTemplateDto>(File.ReadAllText(file, Encoding.UTF8));
                if (template is null
                    || string.IsNullOrWhiteSpace(template.Id)
                    || !IsValidReportBuilderTemplateId(template.Id)
                    || string.IsNullOrWhiteSpace(template.Name))
                {
                    continue;
                }

                template.PageSize = template.PageSize == "Letter" ? "Letter" : "A4";
                template.Margins ??= new ReportBuilderMargins();
                template.Html ??= string.Empty;
                results.Add(template);
            }
            catch (Exception ex)
            {
                // One unreadable file must not hide the other templates.
                AppLog.Error(LogCategoryReportTemplates, $"Could not read report template file '{file}'.", ex);
            }
        }

        return results
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Incoming request shape (only the fields these three requests use).</summary>
    private sealed class ReportBuilderRequest
    {
        [JsonPropertyName("type")]
        public string Type { get; init; } = string.Empty;

        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("template")]
        public ReportBuilderTemplateDto? Template { get; init; }
    }

    /// <summary>A saved template — both the on-disk file format and the shape sent to / received from the frontend.</summary>
    private sealed class ReportBuilderTemplateDto
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        /// <summary>"A4" or "Letter".</summary>
        [JsonPropertyName("pageSize")]
        public string? PageSize { get; set; }

        [JsonPropertyName("margins")]
        public ReportBuilderMargins? Margins { get; set; }

        /// <summary>The Findings text exactly as typed/formatted in the builder (HTML).</summary>
        [JsonPropertyName("html")]
        public string? Html { get; set; }

        [JsonPropertyName("createdAt")]
        public string? CreatedAt { get; set; }

        [JsonPropertyName("updatedAt")]
        public string? UpdatedAt { get; set; }
    }

    /// <summary>Page margins in inches.</summary>
    private sealed class ReportBuilderMargins
    {
        [JsonPropertyName("top")]
        public double Top { get; set; } = 1.0;

        [JsonPropertyName("right")]
        public double Right { get; set; } = 1.0;

        [JsonPropertyName("bottom")]
        public double Bottom { get; set; } = 1.0;

        [JsonPropertyName("left")]
        public double Left { get; set; } = 1.0;
    }

    /// <summary>Reply payload for all three report-builder template requests.</summary>
    private sealed class ReportBuilderResult
    {
        [JsonPropertyName("type")]
        public required string Type { get; init; }

        [JsonPropertyName("success")]
        public required bool Success { get; init; }

        [JsonPropertyName("error")]
        public string? Error { get; init; }

        [JsonPropertyName("templates")]
        public List<ReportBuilderTemplateDto>? Templates { get; init; }

        [JsonPropertyName("template")]
        public ReportBuilderTemplateDto? Template { get; init; }
    }
}
