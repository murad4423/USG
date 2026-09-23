namespace UltrasoundApp.Data;

/// <summary>
/// Resolves the on-disk location of the app's data folder and SQLite
/// database file. The repository already has a top-level <c>data/</c>
/// folder (with <c>dicom-incoming/</c>, <c>images-extracted/</c>,
/// <c>logs/</c>) reserved for exactly this kind of runtime data, so this
/// walks up from the running executable to find the repo root (identified
/// by <c>UltrasoundApp.sln</c>) and anchors <c>data/app.db</c> there.
///
/// If no solution file can be found (e.g. a published/installed build with
/// no source tree alongside it), falls back to a <c>data</c> folder next to
/// the executable so the app still works standalone.
/// </summary>
public static class AppPaths
{
    private const string SolutionFileName = "UltrasoundApp.sln";
    private const string DataFolderName = "data";
    private const string DatabaseFileName = "app.db";

    /// <summary>Full path to the <c>data</c> folder that owns the SQLite database.</summary>
    public static string GetDataDirectory()
    {
        string root = FindRepoRoot() ?? AppContext.BaseDirectory;
        return Path.Combine(root, DataFolderName);
    }

    /// <summary>Full path to the SQLite database file (<c>data/app.db</c>).</summary>
    public static string GetDatabasePath()
    {
        return Path.Combine(GetDataDirectory(), DatabaseFileName);
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
