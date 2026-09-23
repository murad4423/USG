using System.Text.Json;
using Microsoft.Data.Sqlite;
using UltrasoundApp.Core.Models;
using UltrasoundApp.Core.Repositories;

namespace UltrasoundApp.Data.Repositories;

/// <summary>ADO.NET (Microsoft.Data.Sqlite) repository for <see cref="ReportTemplate"/> rows.</summary>
public sealed class ReportTemplateRepository : IReportTemplateRepository
{
    private readonly AppDbContext _context;

    public ReportTemplateRepository(AppDbContext context)
    {
        _context = context;
    }

    public ReportTemplate? GetByExamType(string examType)
    {
        using SqliteConnection connection = _context.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT ExamType, HtmlTemplateFilePath, PlaceholderFieldsJson
            FROM ReportTemplates WHERE ExamType = $examType;
            """;
        command.Parameters.AddWithValue("$examType", examType);

        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public IReadOnlyList<ReportTemplate> GetAll()
    {
        var results = new List<ReportTemplate>();

        using SqliteConnection connection = _context.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT ExamType, HtmlTemplateFilePath, PlaceholderFieldsJson FROM ReportTemplates;";

        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(Map(reader));
        }

        return results;
    }

    public void Add(ReportTemplate template)
    {
        using SqliteConnection connection = _context.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ReportTemplates (ExamType, HtmlTemplateFilePath, PlaceholderFieldsJson)
            VALUES ($examType, $filePath, $placeholders);
            """;
        BindParameters(command, template);
        command.ExecuteNonQuery();
    }

    public void Update(ReportTemplate template)
    {
        using SqliteConnection connection = _context.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ReportTemplates
            SET HtmlTemplateFilePath = $filePath, PlaceholderFieldsJson = $placeholders
            WHERE ExamType = $examType;
            """;
        BindParameters(command, template);
        command.ExecuteNonQuery();
    }

    public void Delete(string examType)
    {
        using SqliteConnection connection = _context.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ReportTemplates WHERE ExamType = $examType;";
        command.Parameters.AddWithValue("$examType", examType);
        command.ExecuteNonQuery();
    }

    private static void BindParameters(SqliteCommand command, ReportTemplate template)
    {
        command.Parameters.AddWithValue("$examType", template.ExamType);
        command.Parameters.AddWithValue("$filePath", template.HtmlTemplateFilePath);
        command.Parameters.AddWithValue("$placeholders", JsonSerializer.Serialize(template.PlaceholderFields));
    }

    private static ReportTemplate Map(SqliteDataReader reader)
    {
        string placeholdersJson = reader.GetString(2);

        return new ReportTemplate
        {
            ExamType = reader.GetString(0),
            HtmlTemplateFilePath = reader.GetString(1),
            PlaceholderFields = JsonSerializer.Deserialize<List<string>>(placeholdersJson) ?? new List<string>()
        };
    }
}
