using System.Text.Json.Serialization;
using UltrasoundApp.Core.Models;

namespace UltrasoundApp.Printing;

/// <summary>
/// Hospital branding + page-layout settings for a printed image sheet.
/// Plain data model, deserialized from a JSON file (see
/// <see cref="ImageSheetBrandingTemplateLoader"/>) such as
/// <c>templates/image-sheets/default-branded-template.json</c>.
/// </summary>
public sealed class ImageSheetBrandingTemplate
{
    /// <summary>
    /// Path to the hospital's logo image file, relative to the repo root or
    /// absolute. Optional — if missing/empty, or the file doesn't exist on
    /// disk, <see cref="ImageSheetComposer"/> simply omits the logo rather
    /// than failing.
    /// </summary>
    [JsonPropertyName("logoPath")]
    public string? LogoPath { get; set; }

    [JsonPropertyName("hospitalName")]
    public string HospitalName { get; set; } = string.Empty;

    /// <summary>Address/contact lines, printed one per line under the hospital name.</summary>
    [JsonPropertyName("addressLines")]
    public List<string> AddressLines { get; set; } = new();

    /// <summary>Free-text line shown under the address block in the page header (e.g. a report title).</summary>
    [JsonPropertyName("headerText")]
    public string? HeaderText { get; set; }

    /// <summary>Free-text line shown in the page footer, next to the page number.</summary>
    [JsonPropertyName("footerText")]
    public string? FooterText { get; set; }

    /// <summary>"A4" or "Letter". Defaults to A4 for any other/unrecognized value.</summary>
    [JsonPropertyName("pageSize")]
    public string PageSize { get; set; } = "A4";

    [JsonPropertyName("marginMillimeters")]
    public double MarginMillimeters { get; set; } = 12;

    [JsonPropertyName("cellSpacingPoints")]
    public double CellSpacingPoints { get; set; } = 8;

    /// <summary>
    /// Upper bound on images per page. If more images than this are
    /// selected, <see cref="ImageSheetComposer"/> spills the remainder onto
    /// additional pages (each auto-gridded the same way) rather than
    /// cramming everything onto one page.
    /// </summary>
    [JsonPropertyName("maxImagesPerPage")]
    public int MaxImagesPerPage { get; set; } = 12;

    /// <summary>If true, prints the image's file name under each thumbnail.</summary>
    [JsonPropertyName("showImageCaptions")]
    public bool ShowImageCaptions { get; set; }

    /// <summary>
    /// Built-in fallback used if the template JSON file is missing or
    /// malformed, so a broken/absent template file degrades gracefully
    /// instead of crashing the composer.
    /// </summary>
    public static ImageSheetBrandingTemplate CreateBuiltInDefault() => new()
    {
        LogoPath = null,
        HospitalName = "General Hospital",
        AddressLines = new List<string> { "123 Main Street", "Anytown, ST 00000" },
        HeaderText = "Ultrasound Image Report",
        FooterText = "Confidential — For Clinical Use Only",
        PageSize = "A4",
        MarginMillimeters = 12,
        CellSpacingPoints = 8,
        MaxImagesPerPage = 12,
        ShowImageCaptions = false,
    };

    /// <summary>
    /// Returns a copy of this template with the branding fields Settings
    /// covers (LogoPath, HospitalName, Address, HeaderText, FooterText)
    /// overridden from <paramref name="settings"/> — this is what lets
    /// changing branding in the Settings screen actually change the
    /// printed image sheet, instead of only the static JSON template file
    /// ever mattering. Page-layout fields (PageSize, MarginMillimeters,
    /// CellSpacingPoints, MaxImagesPerPage, ShowImageCaptions) are left
    /// untouched — Settings doesn't cover those, so there's nothing to
    /// override them with.
    ///
    /// Any Settings branding field that's null/blank falls back to THIS
    /// template's own value for that field, rather than blanking it out —
    /// so an operator who's only set, say, the hospital name in Settings
    /// still gets this template's logo/footer rather than losing them.
    /// <see cref="AppSettings.Address"/> (a single string, since that's
    /// how the Settings form edits it) is split on newlines into
    /// <see cref="AddressLines"/>; blank lines are dropped.
    /// </summary>
    public ImageSheetBrandingTemplate ApplySettings(AppSettings settings) => new()
    {
        LogoPath = string.IsNullOrWhiteSpace(settings.LogoPath) ? LogoPath : settings.LogoPath,
        HospitalName = string.IsNullOrWhiteSpace(settings.HospitalName) ? HospitalName : settings.HospitalName,
        AddressLines = string.IsNullOrWhiteSpace(settings.Address)
            ? AddressLines
            : settings.Address
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList(),
        HeaderText = string.IsNullOrWhiteSpace(settings.HeaderText) ? HeaderText : settings.HeaderText,
        FooterText = string.IsNullOrWhiteSpace(settings.FooterText) ? FooterText : settings.FooterText,
        PageSize = PageSize,
        MarginMillimeters = MarginMillimeters,
        CellSpacingPoints = CellSpacingPoints,
        MaxImagesPerPage = MaxImagesPerPage,
        ShowImageCaptions = ShowImageCaptions,
    };
}
