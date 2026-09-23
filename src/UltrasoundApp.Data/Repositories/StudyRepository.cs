using Microsoft.Data.Sqlite;
using UltrasoundApp.Core.Models;
using UltrasoundApp.Core.Repositories;

namespace UltrasoundApp.Data.Repositories;

/// <summary>ADO.NET (Microsoft.Data.Sqlite) repository for <see cref="Study"/> rows.</summary>
public sealed class StudyRepository : IStudyRepository
{
    private readonly AppDbContext _context;

    public StudyRepository(AppDbContext context)
    {
        _context = context;
    }

    public Study? GetById(string studyInstanceUid)
    {
        using SqliteConnection connection = _context.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT StudyInstanceUID, PatientID, ExamType, StudyDateTime, Status
            FROM Studies WHERE StudyInstanceUID = $id;
            """;
        command.Parameters.AddWithValue("$id", studyInstanceUid);

        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public IReadOnlyList<Study> GetAll()
    {
        var results = new List<Study>();

        using SqliteConnection connection = _context.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT StudyInstanceUID, PatientID, ExamType, StudyDateTime, Status
            FROM Studies ORDER BY StudyDateTime DESC;
            """;

        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(Map(reader));
        }

        return results;
    }

    public IReadOnlyList<Study> GetByPatient(string patientId)
    {
        var results = new List<Study>();

        using SqliteConnection connection = _context.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT StudyInstanceUID, PatientID, ExamType, StudyDateTime, Status
            FROM Studies WHERE PatientID = $patientId ORDER BY StudyDateTime DESC;
            """;
        command.Parameters.AddWithValue("$patientId", patientId);

        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(Map(reader));
        }

        return results;
    }

    public void Add(Study study)
    {
        using SqliteConnection connection = _context.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Studies (StudyInstanceUID, PatientID, ExamType, StudyDateTime, Status)
            VALUES ($id, $patientId, $examType, $dateTime, $status);
            """;
        BindParameters(command, study);
        command.ExecuteNonQuery();
    }

    public void Update(Study study)
    {
        using SqliteConnection connection = _context.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Studies
            SET PatientID = $patientId, ExamType = $examType, StudyDateTime = $dateTime, Status = $status
            WHERE StudyInstanceUID = $id;
            """;
        BindParameters(command, study);
        command.ExecuteNonQuery();
    }

    public void Delete(string studyInstanceUid)
    {
        using SqliteConnection connection = _context.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Studies WHERE StudyInstanceUID = $id;";
        command.Parameters.AddWithValue("$id", studyInstanceUid);
        command.ExecuteNonQuery();
    }

    private static void BindParameters(SqliteCommand command, Study study)
    {
        command.Parameters.AddWithValue("$id", study.StudyInstanceUID);
        command.Parameters.AddWithValue("$patientId", study.PatientID);
        command.Parameters.AddWithValue("$examType", study.ExamType);
        command.Parameters.AddWithValue("$dateTime", study.StudyDateTime.ToString("o"));
        command.Parameters.AddWithValue("$status", study.Status);
    }

    private static Study Map(SqliteDataReader reader)
    {
        return new Study
        {
            StudyInstanceUID = reader.GetString(0),
            PatientID = reader.GetString(1),
            ExamType = reader.GetString(2),
            StudyDateTime = DateTime.Parse(reader.GetString(3)),
            Status = reader.GetString(4)
        };
    }
}
