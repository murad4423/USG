using Microsoft.Data.Sqlite;
using UltrasoundApp.Core.Models;
using UltrasoundApp.Core.Repositories;

namespace UltrasoundApp.Data.Repositories;

/// <summary>
/// ADO.NET (Microsoft.Data.Sqlite) repository for <see cref="PrintJob"/> rows.
/// This is an append-only print log — no natural unique key is specified
/// on the model, so only add/read operations are exposed (no
/// update/delete of individual print history entries). See
/// <see cref="IPrintJobRepository"/> (Core) for the abstraction.
/// </summary>
public sealed class PrintJobRepository : IPrintJobRepository
{
    private readonly AppDbContext _context;

    public PrintJobRepository(AppDbContext context)
    {
        _context = context;
    }

    public IReadOnlyList<PrintJob> GetAll()
    {
        var results = new List<PrintJob>();

        using SqliteConnection connection = _context.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT Type, StudyInstanceUID, Timestamp, PrinterUsed
            FROM PrintJobs ORDER BY Timestamp DESC;
            """;

        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(Map(reader));
        }

        return results;
    }

    public IReadOnlyList<PrintJob> GetByStudy(string studyInstanceUid)
    {
        var results = new List<PrintJob>();

        using SqliteConnection connection = _context.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT Type, StudyInstanceUID, Timestamp, PrinterUsed
            FROM PrintJobs WHERE StudyInstanceUID = $studyId ORDER BY Timestamp DESC;
            """;
        command.Parameters.AddWithValue("$studyId", studyInstanceUid);

        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(Map(reader));
        }

        return results;
    }

    public void Add(PrintJob printJob)
    {
        using SqliteConnection connection = _context.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO PrintJobs (Type, StudyInstanceUID, Timestamp, PrinterUsed)
            VALUES ($type, $studyId, $timestamp, $printerUsed);
            """;
        command.Parameters.AddWithValue("$type", printJob.Type.ToString());
        command.Parameters.AddWithValue("$studyId", printJob.StudyInstanceUID);
        command.Parameters.AddWithValue("$timestamp", printJob.Timestamp.ToString("o"));
        command.Parameters.AddWithValue("$printerUsed", printJob.PrinterUsed);
        command.ExecuteNonQuery();
    }

    private static PrintJob Map(SqliteDataReader reader)
    {
        return new PrintJob
        {
            Type = Enum.Parse<PrintJobType>(reader.GetString(0)),
            StudyInstanceUID = reader.GetString(1),
            Timestamp = DateTime.Parse(reader.GetString(2)),
            PrinterUsed = reader.GetString(3)
        };
    }
}
