namespace UltrasoundApp.Printing;

/// <summary>
/// Resolves on-disk locations this project needs (the default branding
/// template, a scratch folder for the "save to disk" print fallback) by
/// walking up from the running executable to find the repo root, the same
/// way <c>UltrasoundApp.Data.AppPaths</c> does.
///
/// This logic is intentionally duplicated rather than shared via a
/// reference to <c>UltrasoundApp.Data</c> — the Printing project is only
/// supposed to depend on <c>UltrasoundApp.Core</c> (see its .csproj), and
/// pulling in the Data project just for a path helper would break that
/// layering for no real benefit.
/// </summary>
public static class PrintingPaths
{
    private const string SolutionFileName = "UltrasoundApp.sln";

    /// <summary>Repo root (folder containing UltrasoundApp.sln), or the executable's folder as a fallback for published/standalone builds.</summary>
    public static string GetRepoRoot() => FindRepoRoot() ?? AppContext.BaseDirectory;

    /// <summary>Full path to <c>templates/image-sheets/default-branded-template.json</c>.</summary>
    public static string GetDefaultBrandingTemplatePath() =>
        Path.Combine(GetRepoRoot(), "templates", "image-sheets", "default-branded-template.json");

    /// <summary>
    /// Full path to <c>data/print-fallback</c> — where <see cref="SilentPrinter"/>
    /// saves a PDF when it can't confirm a silent print (no printer installed,
    /// or the attempt failed). Created on first use if it doesn't exist yet.
    /// </summary>
    public static string GetPrintFallbackDirectory()
    {
        var dir = Path.Combine(GetRepoRoot(), "data", "print-fallback");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Full path to <c>data/print-preview</c> — scratch folder for image-sheet
    /// PDFs composed for the Image Print UI (both the live preview shown in
    /// PrintPreview.tsx and the PDF handed to <see cref="SilentPrinter"/> when
    /// the user clicks Print). Created on first use if it doesn't exist yet.
    /// Kept separate from <see cref="GetPrintFallbackDirectory"/> so a
    /// preview file is never confused with an actual save-to-disk fallback.
    /// </summary>
    public static string GetPrintPreviewDirectory()
    {
        var dir = Path.Combine(GetRepoRoot(), "data", "print-preview");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Full path to <c>data/image-templates</c> — where the Settings screen's
    /// uploaded "Image Template" PDF is stored (see
    /// <see cref="ImageTemplateBackground"/>), along with its cached
    /// rasterized background PNG. Created on first use if it doesn't exist
    /// yet. Also the folder <c>UltrasoundApp.Host.MainForm</c> maps to the
    /// "imagetemplate" virtual host so the frontend can load the cached
    /// background image directly.
    /// </summary>
    public static string GetImageTemplatesDirectory()
    {
        var dir = Path.Combine(GetRepoRoot(), "data", "image-templates");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, SolutionFileName)))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
