using Microsoft.Data.Sqlite;
using UltrasoundApp.Core.Models;
using UltrasoundApp.Core.Repositories;

namespace UltrasoundApp.Data.Repositories;

/// <summary>
/// ADO.NET (Microsoft.Data.Sqlite) repository for <see cref="GeneratedReport"/> rows.
/// One row per study — <c>StudyInstanceUID</c> is the primary key.
/// </summary>
public sealed class GeneratedReportRepository : IGeneratedReportRepository
{
    private readonly AppDbContext _context;

    public GeneratedReportRepository(AppDbContext context)
    {
        _context = context;
    }

    public GeneratedReport? GetByStudy(string studyInstanceUid)
    {
        using SqliteConnection connection = _context.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT StudyInstanceUID, FieldValuesJson, GeneratedPdfPath, CreatedAt, ModifiedAt
            FROM GeneratedReports WHERE StudyInstanceUID = $studyId;
            """;
        command.Parameters.AddWithValue("$studyId", studyInstanceUid);

        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public IReadOnlyList<GeneratedReport> GetAll()
    {
        var results = new List<GeneratedReport>();

        using SqliteConnection connection = _context.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT StudyInstanceUID, FieldValuesJson, GeneratedPdfPath, CreatedAt, ModifiedAt
            FROM GeneratedReports ORDER BY ModifiedAt DESC;
            """;

        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(Map(reader));
        }

        return results;
    }

    public void Add(GeneratedReport report)
    {
        using SqliteConnection connection = _context.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO GeneratedReports (StudyInstanceUID, FieldValuesJson, GeneratedPdfPath, CreatedAt, ModifiedAt)
            VALUES ($studyId, $fieldValues, $pdfPath, $createdAt, $modifiedAt);
            """;
        BindParameters(command, report);
        command.ExecuteNonQuery();
    }

    public void Update(GeneratedReport report)
    {
        using SqliteConnection connection = _context.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE GeneratedReports
            SET FieldValuesJson = $fieldValues, GeneratedPdfPath = $pdfPath,
                CreatedAt = $createdAt, ModifiedAt = $modifiedAt
            WHERE StudyInstanceUID = $studyId;
            """;
        BindParameters(command, report);
        command.ExecuteNonQuery();
    }

    public void Delete(string studyInstanceUid)
    {
        using SqliteConnection connection = _context.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM GeneratedReports WHERE StudyInstanceUID = $studyId;";
        command.Parameters.AddWithValue("$studyId", studyInstanceUid);
        command.ExecuteNonQuery();
    }

    private static void BindParameters(SqliteCommand command, GeneratedReport report)
    {
        command.Parameters.AddWithValue("$studyId", report.StudyInstanceUID);
        command.Parameters.AddWithValue("$fieldValues", report.FieldValuesJson);
        command.Parameters.AddWithValue("$pdfPath", (object?)report.GeneratedPdfPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", report.CreatedAt.ToString("o"));
        command.Parameters.AddWithValue("$modifiedAt", report.ModifiedAt.ToString("o"));
    }

    private static GeneratedReport Map(SqliteDataReader reader)
    {
        return new GeneratedReport
        {
            StudyInstanceUID = reader.GetString(0),
            FieldValuesJson = reader.GetString(1),
            GeneratedPdfPath = reader.IsDBNull(2) ? null : reader.GetString(2),
            CreatedAt = DateTime.Parse(reader.GetString(3)),
            ModifiedAt = DateTime.Parse(reader.GetString(4))
        };
    }
}
