namespace UltrasoundApp.Templating;

/// <summary>
/// Resolves on-disk locations this project needs (the report template
/// files, a dedicated WebView2 user-data folder, a scratch folder for
/// generated report PDFs) by walking up from the running executable to
/// find the repo root, the same way <c>UltrasoundApp.Data.AppPaths</c> and
/// <c>UltrasoundApp.Printing.PrintingPaths</c> do.
///
/// This logic is intentionally duplicated rather than shared via a
/// reference to <c>UltrasoundApp.Data</c> or <c>UltrasoundApp.Printing</c>
/// — this project is only supposed to depend on <c>UltrasoundApp.Core</c>
/// (see its .csproj), and pulling in another project just for a path
/// helper would break that layering for no real benefit.
/// </summary>
public static class TemplatingPaths
{
    private const string SolutionFileName = "UltrasoundApp.sln";

    /// <summary>Repo root (folder containing UltrasoundApp.sln), or the executable's folder as a fallback for published/standalone builds.</summary>
    public static string GetRepoRoot() => FindRepoRoot() ?? AppContext.BaseDirectory;

    /// <summary>Full path to the <c>templates/reports</c> folder that holds the HTML report templates.</summary>
    public static string GetReportTemplatesDirectory() => Path.Combine(GetRepoRoot(), "templates", "reports");

    /// <summary>Full path to a specific report template file under <c>templates/reports</c> (e.g. <c>"whole-abdomen.html"</c>).</summary>
    public static string GetReportTemplatePath(string fileName) => Path.Combine(GetReportTemplatesDirectory(), fileName);

    /// <summary>
    /// Full path to a dedicated WebView2 user-data folder for
    /// <see cref="PdfRenderer"/>'s hidden headless instance. Kept separate
    /// from the WebView2 folder the visible <c>UltrasoundApp.Host</c> shell
    /// uses so the two never contend over the same profile/cache. Created
    /// on first use if it doesn't exist yet.
    /// </summary>
    public static string GetWebView2UserDataDirectory()
    {
        var dir = Path.Combine(GetRepoRoot(), "data", "webview2-templating-cache");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Full path to <c>data/generated-reports</c> — scratch folder for
    /// rendered report PDFs (used by the manual test harness for now; the
    /// real report pipeline will decide its own final storage location in
    /// a later step). Created on first use if it doesn't exist yet.
    /// </summary>
    public static string GetGeneratedReportsScratchDirectory()
    {
        var dir = Path.Combine(GetRepoRoot(), "data", "generated-reports");
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
