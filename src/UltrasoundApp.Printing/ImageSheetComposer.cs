using QuestPDF.Fluent;
using UltrasoundApp.Core.Logging;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace UltrasoundApp.Printing;

/// <summary>Result of composing an image sheet PDF.</summary>
/// <param name="OutputPdfPath">Where the PDF was written.</param>
/// <param name="PageCount">Number of pages the images were spread across.</param>
/// <param name="ImagesComposed">How many of the requested images were actually found on disk and included.</param>
/// <param name="SkippedImagePaths">Requested paths that did not exist on disk and were silently omitted.</param>
public sealed record ImageSheetComposeResult(
    string OutputPdfPath,
    int PageCount,
    int ImagesComposed,
    IReadOnlyList<string> SkippedImagePaths);

/// <summary>
/// Composes a set of selected image files, plus hospital branding info,
/// into a single print-ready PDF. The grid used per page is computed from
/// how many images are on that page — there is no single fixed layout;
/// see <see cref="CalculateGridDimensions"/>.
/// </summary>
public sealed class ImageSheetComposer
{
    private const string LogCategory = "PRINT";

    /// <summary>
    /// Font fallback chain for every piece of text in a composed image
    /// sheet, in priority order. QuestPDF passes this whole list to Skia's
    /// text shaper, which picks, per character, the first family that
    /// actually has a glyph for it.
    ///
    /// <para>
    /// <b>Why this is needed:</b> QuestPDF's built-in default is Lato
    /// alone (see <c>TextStyle.LibraryDefault</c>), and Lato has no
    /// Bengali glyphs — so Bangla branding text (hospital name, address,
    /// header/footer) rendered as tofu boxes. Unlike the report PDFs,
    /// which go through Chromium (see <c>PdfRenderer</c>) and inherit
    /// Windows' own system-wide font fallback for free, QuestPDF only
    /// falls back across the families named here, so Bangla coverage has
    /// to be requested explicitly.
    /// </para>
    ///
    /// <para>
    /// "Segoe UI" leads so Latin text keeps the same look as the rest of
    /// the app. "Nirmala UI" is the Bangla workhorse — it ships with
    /// Windows 10/11 and covers Bengali, so it needs no installation on a
    /// normal hospital machine. "Noto Sans Bengali" and "Vrinda" follow as
    /// extra safety nets (Noto if the operator installed it; Vrinda is the
    /// older Windows Bengali font). QuestPDF resolves system-installed
    /// fonts by name, so none of these need to be bundled — but if a
    /// deployment machine somehow lacks all of them, bundle a .ttf and
    /// register it via <c>FontManager.RegisterFont</c> instead.
    /// </para>
    /// </summary>
    private static readonly string[] TextFontFamilies =
    {
        "Segoe UI",
        "Nirmala UI",
        "Noto Sans Bengali",
        "Vrinda"
    };

    /// <summary>Fill colour of the box printed behind each image when the "Gray" option is on. Must match <c>.sheet-preview__cell--gray</c> in patient-detail-layout.css.</summary>
    private const string GrayBoxColor = "#1C1F24";

    /// <summary>
    /// Fallback font size for a patient-information placeholder that has no
    /// explicit size (saved by the first version): its box height (in points)
    /// multiplied by this. Must match <c>PLACEHOLDER_FONT_RATIO</c> in the
    /// frontend's placeholderFields.ts so the preview and the printed page
    /// agree.
    /// </summary>
    private const float PlaceholderFontHeightRatio = 0.62f;

    private const float PlaceholderMinFontPoints = 4f;
    private const float PlaceholderMaxFontPoints = 72f;

    static ImageSheetComposer()
    {
        // Community license: free for organizations under $1M USD annual
        // gross revenue. See the licensing note in UltrasoundApp.Printing.csproj —
        // revisit before shipping to a hospital customer above that threshold.
        QuestPDF.Settings.License = LicenseType.Community;
    }

    /// <summary>
    /// Composes <paramref name="imageFilePaths"/> into a PDF at
    /// <paramref name="outputPdfPath"/>, using <paramref name="branding"/>
    /// for the header/footer/logo and page-layout settings.
    /// </summary>
    /// <param name="gridOverride">
    /// When supplied, overrides both how many images are packed onto each
    /// page (Columns × Rows) and the fixed column count used to lay them
    /// out — instead of <see cref="ImageSheetBrandingTemplate.MaxImagesPerPage"/>
    /// and the near-square grid <see cref="CalculateGridDimensions"/> would
    /// otherwise compute from the image count. This is how the operator's
    /// chosen "grid layout" (e.g. 3×2, 4×3) from the frontend actually
    /// changes the composed sheet, rather than only the automatic
    /// per-page-count layout ever being used.
    /// </param>
    /// <param name="backgroundImagePath">
    /// Path to a full-page background image (the Settings screen's uploaded
    /// "Image Template", already rasterized to a PNG by
    /// <see cref="ImageTemplateBackground"/>), or null/missing to use a
    /// plain white page. When supplied, it's drawn edge-to-edge behind
    /// everything (ignoring margins, like a letterhead) and the built-in
    /// text header/footer (<see cref="ComposeHeader"/>/<see cref="ComposeFooter"/>)
    /// are skipped — the uploaded template is assumed to already carry the
    /// hospital's own branding, so drawing both would duplicate/clash.
    /// </param>
    /// <param name="contentArea">
    /// The operator's chosen "image area" on the template page (saved from
    /// the Settings screen, see <see cref="ImageTemplateContentAreaStore"/>),
    /// as fractions of the page. Only used together with a background
    /// image. When supplied, the image grid is confined to exactly this
    /// rectangle — so the template's header/footer outside it stay visible
    /// — and each grid row is sized to fill the rectangle's height, the
    /// same way the live preview does. When null, the grid uses the normal
    /// page margins as before.
    /// </param>
    /// <param name="grayBackground">
    /// Only used together with <paramref name="contentArea"/>. When true,
    /// every image is printed on a dark box (<see cref="GrayBoxColor"/>,
    /// the same look as the "Gray" option in the live preview). When false,
    /// the images are printed clean, with nothing behind or around them.
    /// </param>
    /// <param name="placeholders">
    /// The patient-information boxes the operator placed on the template
    /// (see <see cref="ImageTemplatePlaceholderStore"/>). Only used together
    /// with a background image, and only together with
    /// <paramref name="placeholderValues"/>.
    /// </param>
    /// <param name="placeholderValues">
    /// The text to print per placeholder field (field key -> value), e.g.
    /// <c>patientName</c> -> "MAHMUDA AKTER". A placeholder whose field has no
    /// value (or a blank one) prints nothing. Each box is drawn on EVERY page
    /// of the sheet, on top of the images.
    /// </param>
    /// <exception cref="ArgumentException">No image paths were supplied.</exception>
    /// <exception cref="FileNotFoundException">None of the supplied image paths exist on disk.</exception>
    public ImageSheetComposeResult Compose(
        IReadOnlyList<string> imageFilePaths,
        ImageSheetBrandingTemplate branding,
        string outputPdfPath,
        (int Columns, int Rows)? gridOverride = null,
        string? backgroundImagePath = null,
        ImageTemplateContentArea? contentArea = null,
        bool grayBackground = false,
        IReadOnlyList<ImageTemplatePlaceholder>? placeholders = null,
        IReadOnlyDictionary<string, string>? placeholderValues = null)
    {
        if (imageFilePaths is null || imageFilePaths.Count == 0)
        {
            throw new ArgumentException("At least one image path is required.", nameof(imageFilePaths));
        }

        var existingPaths = new List<string>();
        var skippedPaths = new List<string>();
        foreach (var path in imageFilePaths)
        {
            if (File.Exists(path))
            {
                existingPaths.Add(path);
            }
            else
            {
                skippedPaths.Add(path);
            }
        }

        if (existingPaths.Count == 0)
        {
            throw new FileNotFoundException("None of the supplied image paths exist on disk.");
        }

        var pageSize = ResolvePageSize(branding.PageSize);
        var imagesPerPage = gridOverride.HasValue
            ? Math.Max(1, gridOverride.Value.Columns * gridOverride.Value.Rows)
            : Math.Max(1, branding.MaxImagesPerPage);
        var pages = Chunk(existingPaths, imagesPerPage);
        var fixedColumns = gridOverride?.Columns;
        var fixedRows = gridOverride?.Rows;

        var outputDirectory = Path.GetDirectoryName(outputPdfPath);
        if (!string.IsNullOrEmpty(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        bool hasBackground = !string.IsNullOrWhiteSpace(backgroundImagePath) && File.Exists(backgroundImagePath);

        // The image-area rectangle only makes sense on top of a template
        // background (it marks the free space between the template's own
        // header and footer).
        ImageTemplateContentArea? area = hasBackground && contentArea is { IsValid: true } ? contentArea : null;
        float pageWidthPoints = pageSize.Width;
        float pageHeightPoints = pageSize.Height;

        // Patient-information boxes with the text they print for this
        // patient. Like the image area, they only make sense on top of a
        // template background; boxes with no value for this patient are
        // skipped.
        var placeholderItems = new List<(ImageTemplatePlaceholder Box, string Text)>();
        if (hasBackground && placeholders is not null && placeholderValues is not null)
        {
            foreach (var box in placeholders)
            {
                if (box is { IsValid: true }
                    && placeholderValues.TryGetValue(box.Field, out var text)
                    && !string.IsNullOrWhiteSpace(text))
                {
                    placeholderItems.Add((box, text.Trim()));
                }
            }
        }

        var document = Document.Create(container =>
        {
            foreach (var pageImages in pages)
            {
                container.Page(page =>
                {
                    page.Size(pageSize);

                    // With an image area the position comes from the area's
                    // own offsets (below), so the page itself gets no margin.
                    page.Margin(area is null ? (float)branding.MarginMillimeters : 0f, Unit.Millimetre);

                    if (hasBackground)
                    {
                        // Background()/Foreground() cover the FULL page
                        // (ignoring Margin above), unlike Header/Content/
                        // Footer — exactly what's needed for a full-bleed
                        // letterhead image with the image grid still
                        // confined to the normal margin area on top of it.
                        page.Background().Image(backgroundImagePath!).FitArea();
                    }
                    else
                    {
                        page.PageColor(Colors.White);
                    }

                    // FontFamily here (rather than per-Text-call) makes the
                    // Bangla fallback chain the default every nested
                    // .Text(...) inherits — header, footer, captions and
                    // page numbers alike — so no call site can silently
                    // drop back to Lato and re-introduce tofu boxes.
                    page.DefaultTextStyle(x => x.FontSize(9).FontFamily(TextFontFamilies));

                    // The uploaded template PDF is assumed to already carry
                    // the hospital's own header/footer/logo — skip the
                    // built-in text branding so it isn't drawn twice.
                    if (!hasBackground)
                    {
                        page.Header().Element(c => ComposeHeader(c, branding));
                    }

                    if (area is not null)
                    {
                        // Confine the grid to the chosen rectangle: pad the
                        // content box by the area's distance from each page
                        // edge, leaving exactly the area's size for the grid.
                        float left = (float)(area.X * pageWidthPoints);
                        float top = (float)(area.Y * pageHeightPoints);
                        float right = (float)((1 - area.X - area.Width) * pageWidthPoints);
                        float bottom = (float)((1 - area.Y - area.Height) * pageHeightPoints);
                        float areaHeightPoints = (float)(area.Height * pageHeightPoints);

                        page.Content()
                            .PaddingLeft(left, Unit.Point)
                            .PaddingTop(top, Unit.Point)
                            .PaddingRight(Math.Max(0f, right), Unit.Point)
                            .PaddingBottom(Math.Max(0f, bottom), Unit.Point)
                            .Element(c => ComposeImageGrid(c, pageImages, branding, fixedColumns, fixedRows, areaHeightPoints, grayBackground));
                    }
                    else
                    {
                        page.Content().PaddingVertical(10).Element(c => ComposeImageGrid(c, pageImages, branding, fixedColumns));
                    }

                    if (!hasBackground)
                    {
                        page.Footer().Element(c => ComposeFooter(c, branding));
                    }

                    // Patient information (name, ID, age...) at the spots the
                    // operator marked on the template. Foreground() covers the
                    // FULL page (ignoring the margin/area padding above), so
                    // the page fractions map straight onto the page.
                    if (placeholderItems.Count > 0)
                    {
                        page.Foreground().Layers(layers =>
                        {
                            // The primary layer only defines the size of the
                            // layer stack: make it the whole page.
                            layers.PrimaryLayer().Extend();

                            foreach (var (box, text) in placeholderItems)
                            {
                                ComposePlaceholder(layers.Layer(), box, text, pageWidthPoints, pageHeightPoints);
                            }
                        });
                    }
                });
            }
        });

        if (skippedPaths.Count > 0)
        {
            AppLog.Warning(
                LogCategory,
                $"{skippedPaths.Count} image(s) referenced by this study were missing from disk and were omitted " +
                $"from the sheet: {string.Join(", ", skippedPaths)}");
        }

        try
        {
            document.GeneratePdf(outputPdfPath);
        }
        catch (Exception ex)
        {
            // Most likely causes: a glyph with no font coverage (QuestPDF
            // throws rather than rendering tofu when a debugger is
            // attached), an unreadable/corrupt image file that survived
            // the File.Exists check above, or the output path not being
            // writable.
            AppLog.Error(LogCategory, $"Failed to compose the image sheet PDF at '{outputPdfPath}'.", ex);
            throw new ImageSheetComposeException(
                "The image sheet could not be created. One of the images may be damaged, " +
                "or the branding text may contain characters the available fonts can't display.", ex);
        }

        AppLog.Info(
            LogCategory,
            $"Composed image sheet '{outputPdfPath}': {existingPaths.Count} image(s) across {pages.Count} page(s)" +
            (skippedPaths.Count > 0 ? $", {skippedPaths.Count} skipped." : "."));

        return new ImageSheetComposeResult(outputPdfPath, pages.Count, existingPaths.Count, skippedPaths);
    }

    /// <summary>
    /// Computes an (columns, rows) grid for a given number of images: a
    /// near-square grid that grows with the count instead of a single
    /// hardcoded layout. E.g. 1 → 1x1, 4 → 2x2, 5 → 3x2, 6 → 3x2, 9 → 3x3.
    /// </summary>
    public static (int Columns, int Rows) CalculateGridDimensions(int imageCount)
    {
        if (imageCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(imageCount), "Image count must be positive.");
        }

        var columns = (int)Math.Ceiling(Math.Sqrt(imageCount));
        var rows = (int)Math.Ceiling(imageCount / (double)columns);
        return (columns, rows);
    }

    /// <summary>
    /// Draws one patient-information box: a single line of text, vertically
    /// centred in the box, aligned left/center/right, cut off with "…" if it
    /// doesn't fit — the same look the live preview gives it. Font size, font
    /// family and colour come from the box's own settings.
    /// </summary>
    private static void ComposePlaceholder(
        IContainer container,
        ImageTemplatePlaceholder box,
        string text,
        float pageWidthPoints,
        float pageHeightPoints)
    {
        float left = (float)(box.X * pageWidthPoints);
        float top = (float)(box.Y * pageHeightPoints);
        float width = (float)(box.Width * pageWidthPoints);
        float height = (float)(box.Height * pageHeightPoints);
        float fontSize = Math.Clamp(
            box.FontSize > 0 ? (float)box.FontSize : height * PlaceholderFontHeightRatio,
            PlaceholderMinFontPoints,
            PlaceholderMaxFontPoints);

        // The chosen font first, then the usual fallbacks (so Bangla text still
        // finds a font even if the chosen one lacks those letters / isn't installed).
        string[] fontFamilies = string.IsNullOrWhiteSpace(box.FontFamily)
            ? TextFontFamilies
            : new[] { box.FontFamily }.Concat(TextFontFamilies).ToArray();
        string textColor = string.IsNullOrWhiteSpace(box.Color) ? "#000000" : box.Color;

        // Translate (rather than padding) positions the box without
        // shrinking the space it is measured in, so a box touching the
        // page's right/bottom edge can never be over-constrained by
        // rounding.
        IContainer positioned = container
            .TranslateX(left, Unit.Point)
            .TranslateY(top, Unit.Point)
            .Width(width, Unit.Point)
            .Height(height, Unit.Point);

        IContainer aligned = (box.Align ?? string.Empty).ToLowerInvariant() switch
        {
            "center" => positioned.AlignCenter(),
            "right" => positioned.AlignRight(),
            _ => positioned.AlignLeft(),
        };

        aligned.AlignMiddle().Text(t =>
        {
            t.DefaultTextStyle(style =>
            {
                var styled = style.FontSize(fontSize).FontFamily(fontFamilies).FontColor(textColor);
                return box.Bold ? styled.Bold() : styled;
            });
            t.ClampLines(1);
            t.Span(text);
        });
    }

    private static void ComposeHeader(IContainer container, ImageSheetBrandingTemplate branding)
    {
        container.Column(column =>
        {
            column.Item().Row(row =>
            {
                if (!string.IsNullOrWhiteSpace(branding.LogoPath) && File.Exists(branding.LogoPath))
                {
                    row.ConstantItem(70).Height(50).Image(branding.LogoPath).FitArea();
                    row.ConstantItem(10);
                }

                row.RelativeItem().Column(col =>
                {
                    col.Item().Text(string.IsNullOrWhiteSpace(branding.HospitalName) ? "Hospital" : branding.HospitalName)
                        .Bold().FontSize(14);

                    foreach (var line in branding.AddressLines)
                    {
                        col.Item().Text(line).FontSize(8).FontColor(Colors.Grey.Darken1);
                    }

                    if (!string.IsNullOrWhiteSpace(branding.HeaderText))
                    {
                        col.Item().PaddingTop(2).Text(branding.HeaderText).FontSize(9).Italic();
                    }
                });
            });

            column.Item().PaddingTop(4).LineHorizontal(1).LineColor(Colors.Grey.Lighten1);
        });
    }

    private static void ComposeFooter(IContainer container, ImageSheetBrandingTemplate branding)
    {
        container.Column(column =>
        {
            column.Item().LineHorizontal(1).LineColor(Colors.Grey.Lighten1);
            column.Item().PaddingTop(4).Row(row =>
            {
                row.RelativeItem().Text(branding.FooterText ?? string.Empty).FontSize(8).FontColor(Colors.Grey.Darken1);

                row.RelativeItem().AlignRight().Text(text =>
                {
                    text.DefaultTextStyle(x => x.FontSize(8).FontColor(Colors.Grey.Darken1));
                    text.CurrentPageNumber();
                    text.Span(" / ");
                    text.TotalPages();
                });
            });
        });
    }

    private static void ComposeImageGrid(
        IContainer container,
        IReadOnlyList<string> imagePaths,
        ImageSheetBrandingTemplate branding,
        int? fixedColumns = null,
        int? fixedRows = null,
        float? availableHeightPoints = null,
        bool grayBox = false)
    {
        var columns = fixedColumns ?? CalculateGridDimensions(imagePaths.Count).Columns;
        var cellPadding = (float)(branding.CellSpacingPoints / 2.0);

        // With an image area on a template page, the cells are drawn
        // "clean": no grey frame around each image and no extra inner
        // padding, so nothing but the images themselves shows on top of the
        // template (the spacing between images still comes from cellPadding).
        // Without an image area the original framed cells are kept.
        bool framed = !availableHeightPoints.HasValue;

        // Optional dark box behind each image (image-area mode only): solid
        // fill + 4pt inner padding, matching the preview's "Gray" option.
        // The box adds 8pt (2 * 4pt padding) to what a row needs.
        bool gray = availableHeightPoints.HasValue && grayBox;

        // Fixed-height cells that exactly fill the image area (used only
        // when an image area was chosen). Each table row is
        //   outer padding (2 * cellPadding) + the image cell,
        // so the image cell gets whatever is left of one row's share of the
        // area. Everything else (no image area) keeps the original fixed
        // 150pt / 130pt cell heights.
        float? areaCellHeight = null;
        if (availableHeightPoints.HasValue)
        {
            var rowCount = Math.Max(1, fixedRows ?? (int)Math.Ceiling(imagePaths.Count / (double)columns));
            var rowHeight = availableHeightPoints.Value / rowCount;
            var chrome = (2 * cellPadding) + (gray ? 8f : 0f);
            // Small safety margin so rounding can never push the last row onto a new page.
            areaCellHeight = Math.Max(20f, rowHeight - chrome - 1f);
        }

        container.Table(table =>
        {
            table.ColumnsDefinition(columnsDefinition =>
            {
                for (var i = 0; i < columns; i++)
                {
                    columnsDefinition.RelativeColumn();
                }
            });

            for (var index = 0; index < imagePaths.Count; index++)
            {
                var row = index / columns;
                var col = index % columns;
                var imagePath = imagePaths[index];

                IContainer cellContainer = table.Cell().Row((uint)(row + 1)).Column((uint)(col + 1))
                    .Padding(cellPadding);

                if (framed)
                {
                    cellContainer = cellContainer
                        .Border(1)
                        .BorderColor(Colors.Grey.Lighten2)
                        .Padding(4);
                }
                else if (gray)
                {
                    // The box is given an explicit height (the whole row's
                    // share: image height + 2 * 4pt padding) and Extend() so
                    // it always fills its entire cell. Without that, the
                    // fill only wrapped the (aspect-fitted) image itself and
                    // was completely hidden behind it.
                    cellContainer = cellContainer
                        .Height((areaCellHeight ?? 150f) + 8f)
                        .Extend()
                        .Background(GrayBoxColor)
                        .Padding(4);
                }

                cellContainer
                    .Element(cell =>
                    {
                        if (branding.ShowImageCaptions)
                        {
                            cell.Column(cellColumn =>
                            {
                                cellColumn.Item().Height(areaCellHeight.HasValue ? Math.Max(10f, areaCellHeight.Value - 14f) : 130)
                                    .AlignCenter().AlignMiddle().Image(imagePath).FitArea();
                                cellColumn.Item().AlignCenter().PaddingTop(2).Text(Path.GetFileName(imagePath)).FontSize(7);
                            });
                        }
                        else if (gray)
                        {
                            // The box above already supplies the height; just centre the image in it.
                            cell.AlignCenter().AlignMiddle().Image(imagePath).FitArea();
                        }
                        else
                        {
                            // AlignCenter/AlignMiddle: FitArea keeps the image's own
                            // aspect ratio, so without them a narrower-than-cell
                            // image sticks to the top-left corner of its cell.
                            cell.Height(areaCellHeight ?? 150).AlignCenter().AlignMiddle().Image(imagePath).FitArea();
                        }
                    });
            }
        });
    }

    private static List<List<string>> Chunk(List<string> source, int chunkSize)
    {
        var result = new List<List<string>>();
        for (var i = 0; i < source.Count; i += chunkSize)
        {
            result.Add(source.GetRange(i, Math.Min(chunkSize, source.Count - i)));
        }

        return result;
    }

    private static PageSize ResolvePageSize(string? pageSize) => pageSize?.Trim().ToUpperInvariant() switch
    {
        "LETTER" => PageSizes.Letter,
        _ => PageSizes.A4,
    };
}
