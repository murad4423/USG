using System.Text.Json;

namespace UltrasoundApp.Printing;

/// <summary>
/// Loads an <see cref="ImageSheetBrandingTemplate"/> from a JSON file.
/// Never throws for a missing or malformed template file — it falls back
/// to <see cref="ImageSheetBrandingTemplate.CreateBuiltInDefault"/> instead,
/// since a bad template file shouldn't stop image printing from working at
/// all.
/// </summary>
public static class ImageSheetBrandingTemplateLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Loads <c>templates/image-sheets/default-branded-template.json</c> from the repo root.</summary>
    public static ImageSheetBrandingTemplate LoadDefault() => Load(PrintingPaths.GetDefaultBrandingTemplatePath());

    /// <summary>Loads a branding template from an arbitrary JSON file path.</summary>
    public static ImageSheetBrandingTemplate Load(string jsonFilePath)
    {
        try
        {
            if (!File.Exists(jsonFilePath))
            {
                return ImageSheetBrandingTemplate.CreateBuiltInDefault();
            }

            var json = File.ReadAllText(jsonFilePath);
            var template = JsonSerializer.Deserialize<ImageSheetBrandingTemplate>(json, JsonOptions);
            return template ?? ImageSheetBrandingTemplate.CreateBuiltInDefault();
        }
        catch (JsonException)
        {
            // Malformed template file — degrade gracefully rather than
            // taking down image printing over a typo in a config file.
            return ImageSheetBrandingTemplate.CreateBuiltInDefault();
        }
    }
}
