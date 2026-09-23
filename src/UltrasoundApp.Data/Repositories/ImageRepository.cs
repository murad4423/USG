using Microsoft.Data.Sqlite;
using UltrasoundApp.Core.Models;
using UltrasoundApp.Core.Repositories;

namespace UltrasoundApp.Data.Repositories;

/// <summary>ADO.NET (Microsoft.Data.Sqlite) repository for <see cref="Image"/> rows.</summary>
public sealed class ImageRepository : IImageRepository
{
    private readonly AppDbContext _context;

    public ImageRepository(AppDbContext context)
    {
        _context = context;
    }

    public Image? GetById(string sopInstanceUid)
    {
        using SqliteConnection connection = _context.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT SOPInstanceUID, StudyInstanceUID, FilePath, ThumbnailPath
            FROM Images WHERE SOPInstanceUID = $id;
            """;
        command.Parameters.AddWithValue("$id", sopInstanceUid);

        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public IReadOnlyList<Image> GetByStudy(string studyInstanceUid)
    {
        var results = new List<Image>();

        using SqliteConnection connection = _context.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT SOPInstanceUID, StudyInstanceUID, FilePath, ThumbnailPath
            FROM Images WHERE StudyInstanceUID = $studyId;
            """;
        command.Parameters.AddWithValue("$studyId", studyInstanceUid);

        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(Map(reader));
        }

        return results;
    }

    public void Add(Image image)
    {
        using SqliteConnection connection = _context.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Images (SOPInstanceUID, StudyInstanceUID, FilePath, ThumbnailPath)
            VALUES ($id, $studyId, $filePath, $thumbnailPath);
            """;
        BindParameters(command, image);
        command.ExecuteNonQuery();
    }

    public void Update(Image image)
    {
        using SqliteConnection connection = _context.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Images
            SET StudyInstanceUID = $studyId, FilePath = $filePath, ThumbnailPath = $thumbnailPath
            WHERE SOPInstanceUID = $id;
            """;
        BindParameters(command, image);
        command.ExecuteNonQuery();
    }

    public void Delete(string sopInstanceUid)
    {
        using SqliteConnection connection = _context.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Images WHERE SOPInstanceUID = $id;";
        command.Parameters.AddWithValue("$id", sopInstanceUid);
        command.ExecuteNonQuery();
    }

    private static void BindParameters(SqliteCommand command, Image image)
    {
        command.Parameters.AddWithValue("$id", image.SOPInstanceUID);
        command.Parameters.AddWithValue("$studyId", image.StudyInstanceUID);
        command.Parameters.AddWithValue("$filePath", image.FilePath);
        command.Parameters.AddWithValue("$thumbnailPath", (object?)image.ThumbnailPath ?? DBNull.Value);
    }

    private static Image Map(SqliteDataReader reader)
    {
        return new Image
        {
            SOPInstanceUID = reader.GetString(0),
            StudyInstanceUID = reader.GetString(1),
            FilePath = reader.GetString(2),
            ThumbnailPath = reader.IsDBNull(3) ? null : reader.GetString(3)
        };
    }
}
