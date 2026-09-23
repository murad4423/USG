namespace UltrasoundApp.Dicom;

/// <summary>
/// Resolves the on-disk location of the <c>data/images-extracted</c>
/// folder that this project writes converted images and thumbnails into.
///
/// This deliberately does not depend on UltrasoundApp.Data (parsing stays
/// self-contained for this step) — it duplicates the same small
/// "walk up to the repo root" logic used there, anchored on the same
/// <c>UltrasoundApp.sln</c> marker file, so both projects agree on where
/// <c>data/</c> lives without one referencing the other.
/// </summary>
public static class ImageStoragePaths
{
    private const string SolutionFileName = "UltrasoundApp.sln";
    private const string DataFolderName = "data";
    private const string ImagesExtractedFolderName = "images-extracted";
    private const string DicomIncomingFolderName = "dicom-incoming";

    /// <summary>Full path to the <c>data/images-extracted</c> folder, creating it if missing.</summary>
    public static string GetImagesExtractedDirectory()
    {
        string root = FindRepoRoot() ?? AppContext.BaseDirectory;
        string path = Path.Combine(root, DataFolderName, ImagesExtractedFolderName);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Full path to the <c>data/dicom-incoming</c> folder, creating it if
    /// missing. This is where <see cref="DicomScpListener"/> saves each
    /// C-STORE-received file before handing it to <see cref="DicomFileParser"/>
    /// — the same parser the manual upload path uses on a file the operator
    /// picked from disk, so auto-received files need a real path on disk
    /// too rather than being parsed purely in-memory.
    /// </summary>
    public static string GetDicomIncomingDirectory()
    {
        string root = FindRepoRoot() ?? AppContext.BaseDirectory;
        string path = Path.Combine(root, DataFolderName, DicomIncomingFolderName);
        Directory.CreateDirectory(path);
        return path;
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
