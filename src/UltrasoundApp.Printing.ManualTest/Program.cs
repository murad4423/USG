// Throwaway manual test harness for UltrasoundApp.Printing — NOT part of
// the shipped app. Generates a handful of synthetic sample images (so
// nobody needs real DICOM exports handy), then calls ImageSheetComposer
// and SilentPrinter directly, the way the task for this step asked
// ("test this backend logic directly, e.g. a temporary test call").
//
// Run with:
//   dotnet run --project src/UltrasoundApp.Printing.ManualTest
//
// See docs/README.md ("Image sheet print backend") for the full
// walkthrough of what to check in the console output and generated files.

using System.Drawing;
using System.Drawing.Imaging;
using UltrasoundApp.Printing;

Console.WriteLine("UltrasoundApp.Printing manual test harness");
Console.WriteLine("===========================================");
Console.WriteLine();

var workDir = Path.Combine(Path.GetTempPath(), "ultrasound-printing-manual-test");
Directory.CreateDirectory(workDir);
Console.WriteLine($"Working folder: {workDir}");

var sampleImages = GenerateSampleImages(workDir, count: 6);
Console.WriteLine($"Generated {sampleImages.Count} synthetic sample images.");
Console.WriteLine();

var branding = ImageSheetBrandingTemplateLoader.LoadDefault();
Console.WriteLine("Loaded branding template:");
Console.WriteLine($"  Hospital:        {branding.HospitalName}");
Console.WriteLine($"  Header text:     {branding.HeaderText}");
Console.WriteLine($"  Footer text:     {branding.FooterText}");
Console.WriteLine($"  Max per page:    {branding.MaxImagesPerPage}");
Console.WriteLine();

var composer = new ImageSheetComposer();
var generatedPdfPaths = new List<string>();

foreach (var count in new[] { 1, 4, 5, 6 })
{
    var subset = sampleImages.Take(count).ToList();
    var outputPath = Path.Combine(workDir, $"image-sheet-{count}.pdf");

    var result = composer.Compose(subset, branding, outputPath);
    var (columns, rows) = ImageSheetComposer.CalculateGridDimensions(count);

    Console.WriteLine($"[{count} image(s)] grid = {columns}x{rows}, pages = {result.PageCount}, skipped = {result.SkippedImagePaths.Count}");
    Console.WriteLine($"  -> {result.OutputPdfPath}");

    generatedPdfPaths.Add(result.OutputPdfPath);
}

Console.WriteLine();
Console.WriteLine("Composing a 14-image sheet to confirm pagination beyond MaxImagesPerPage...");
var manyImages = Enumerable.Range(0, 14).Select(i => sampleImages[i % sampleImages.Count]).ToList();
var manyResult = composer.Compose(manyImages, branding, Path.Combine(workDir, "image-sheet-14.pdf"));
Console.WriteLine($"[14 images] pages = {manyResult.PageCount} (expect 2, since MaxImagesPerPage = {branding.MaxImagesPerPage})");
Console.WriteLine($"  -> {manyResult.OutputPdfPath}");
generatedPdfPaths.Add(manyResult.OutputPdfPath);

Console.WriteLine();
Console.WriteLine("Attempting SilentPrinter against the 6-image sheet...");
var printer = new SilentPrinter();
var printResult = printer.Print(Path.Combine(workDir, "image-sheet-6.pdf"));

Console.WriteLine($"  Mode:      {printResult.Mode}");
Console.WriteLine($"  Succeeded: {printResult.Succeeded}");
Console.WriteLine($"  Printer:   {printResult.PrinterUsed ?? "(none installed)"}");
Console.WriteLine($"  Output:    {printResult.OutputPath}");
Console.WriteLine($"  Message:   {printResult.Message}");

Console.WriteLine();
Console.WriteLine("Done. Open the PDFs listed above and confirm:");
Console.WriteLine("  - image-sheet-1.pdf  : one large image, header/footer/branding visible");
Console.WriteLine("  - image-sheet-4.pdf  : 2x2 grid");
Console.WriteLine("  - image-sheet-5.pdf  : 3x2 grid (one empty cell)");
Console.WriteLine("  - image-sheet-6.pdf  : 3x2 grid, all cells filled");
Console.WriteLine("  - image-sheet-14.pdf : 2 pages, each auto-gridded for its own image count");

static List<string> GenerateSampleImages(string directory, int count)
{
    var palette = new[]
    {
        Color.SteelBlue, Color.IndianRed, Color.SeaGreen,
        Color.Goldenrod, Color.MediumPurple, Color.DarkSlateGray,
    };

    var paths = new List<string>();

    for (var i = 1; i <= count; i++)
    {
        var path = Path.Combine(directory, $"sample-{i}.png");

        using var bitmap = new Bitmap(800, 600);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(palette[(i - 1) % palette.Length]);

            using var font = new Font("Arial", 96, FontStyle.Bold);
            var text = $"IMG {i}";
            var textSize = graphics.MeasureString(text, font);
            graphics.DrawString(
                text,
                font,
                Brushes.White,
                (bitmap.Width - textSize.Width) / 2,
                (bitmap.Height - textSize.Height) / 2);
        }

        bitmap.Save(path, ImageFormat.Png);
        paths.Add(path);
    }

    return paths;
}
