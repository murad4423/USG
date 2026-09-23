using System.Text.Json;
using System.Text.Json.Serialization;
using UltrasoundApp.Core.Logging;

namespace UltrasoundApp.Printing;

/// <summary>
/// The rectangle on the uploaded "Image Template" page inside which the
/// selected ultrasound images are allowed to appear — everything outside it
/// (the hospital's own header / footer / logo) stays visible.
///
/// <para>
/// All four values are <b>fractions of the page (0..1)</b>, measured from the
/// page's top-left corner, NOT pixels or points. That way the same saved area
/// works for the Settings editor (which shows the template at whatever size
/// fits the window), for the live preview (any zoom) and for the real PDF
/// composed at print time (<see cref="ImageSheetComposer"/>).
/// </para>
/// </summary>
public sealed class ImageTemplateContentArea
{
    /// <summary>Smallest width/height (as a page fraction) that is accepted — stops an accidental click from saving a 1-pixel area.</summary>
    public const double MinimumSize = 0.05;

    /// <summary>Left edge, as a fraction of the page width.</summary>
    [JsonPropertyName("x")]
    public double X { get; set; }

    /// <summary>Top edge, as a fraction of the page height.</summary>
    [JsonPropertyName("y")]
    public double Y { get; set; }

    /// <summary>Width, as a fraction of the page width.</summary>
    [JsonPropertyName("width")]
    public double Width { get; set; }

    /// <summary>Height, as a fraction of the page height.</summary>
    [JsonPropertyName("height")]
    public double Height { get; set; }

    /// <summary>True when the values describe a usable rectangle that lies inside the page.</summary>
    [JsonIgnore]
    public bool IsValid =>
        !double.IsNaN(X) && !double.IsNaN(Y) && !double.IsNaN(Width) && !double.IsNaN(Height)
        && X >= 0 && Y >= 0
        && Width >= MinimumSize && Height >= MinimumSize
        && X + Width <= 1.0001 && Y + Height <= 1.0001;
}

/// <summary>
/// Saves / loads the operator's chosen <see cref="ImageTemplateContentArea"/>
/// as a small JSON file (<c>content-area.json</c>) in the same
/// <c>data/image-templates</c> folder as the uploaded template PDF — so it
/// survives app restarts and can be re-edited at any time, with no database
/// change.
/// </summary>
public static class ImageTemplateContentAreaStore
{
    private const string LogCategory = "PRINT";
    private const string FileName = "content-area.json";

    private static string GetFilePath() => Path.Combine(PrintingPaths.GetImageTemplatesDirectory(), FileName);

    /// <summary>
    /// Clamps a raw rectangle (as sent by the frontend) into the page and
    /// returns it, or null if it is unusable (NaN, or too small).
    /// </summary>
    public static ImageTemplateContentArea? Normalize(ImageTemplateContentArea? raw)
    {
        if (raw is null)
        {
            return null;
        }

        if (double.IsNaN(raw.X) || double.IsNaN(raw.Y) || double.IsNaN(raw.Width) || double.IsNaN(raw.Height))
        {
            return null;
        }

        double x = Math.Clamp(raw.X, 0, 1);
        double y = Math.Clamp(raw.Y, 0, 1);
        double width = Math.Clamp(raw.Width, 0, 1 - x);
        double height = Math.Clamp(raw.Height, 0, 1 - y);

        var area = new ImageTemplateContentArea { X = x, Y = y, Width = width, Height = height };
        return area.IsValid ? area : null;
    }

    /// <summary>The saved area, or null if none was saved (or the file is unreadable/invalid — then the sheet just uses its normal margins).</summary>
    public static ImageTemplateContentArea? Load()
    {
        try
        {
            string path = GetFilePath();
            if (!File.Exists(path))
            {
                return null;
            }

            var area = JsonSerializer.Deserialize<ImageTemplateContentArea>(File.ReadAllText(path));
            return area is { IsValid: true } ? area : null;
        }
        catch (Exception ex)
        {
            AppLog.Error(LogCategory, "Could not read the saved image-template content area.", ex);
            return null;
        }
    }

    /// <summary>Writes <paramref name="area"/> (already normalized) to disk, replacing any previous one.</summary>
    public static void Save(ImageTemplateContentArea area)
    {
        File.WriteAllText(GetFilePath(), JsonSerializer.Serialize(area));
        AppLog.Info(
            LogCategory,
            $"Saved image-template content area x={area.X:0.###}, y={area.Y:0.###}, w={area.Width:0.###}, h={area.Height:0.###}.");
    }

    /// <summary>Removes the saved area, if any (sheets go back to their normal margins).</summary>
    public static void Clear()
    {
        try
        {
            string path = GetFilePath();
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error(LogCategory, "Could not remove the saved image-template content area.", ex);
        }
    }
}
