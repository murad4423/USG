using Microsoft.Data.Sqlite;
using UltrasoundApp.Core.Models;
using UltrasoundApp.Core.Repositories;

namespace UltrasoundApp.Data.Repositories;

/// <summary>ADO.NET (Microsoft.Data.Sqlite) repository for <see cref="Patient"/> rows.</summary>
public sealed class PatientRepository : IPatientRepository
{
    private readonly AppDbContext _context;

    public PatientRepository(AppDbContext context)
    {
        _context = context;
    }

    public Patient? GetById(string patientId)
    {
        using SqliteConnection connection = _context.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT PatientID, PatientName, DateOfBirth, Age, Sex FROM Patients WHERE PatientID = $id;";
        command.Parameters.AddWithValue("$id", patientId);

        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public IReadOnlyList<Patient> GetAll()
    {
        var results = new List<Patient>();

        using SqliteConnection connection = _context.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT PatientID, PatientName, DateOfBirth, Age, Sex FROM Patients ORDER BY PatientName;";

        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(Map(reader));
        }

        return results;
    }

    public void Add(Patient patient)
    {
        using SqliteConnection connection = _context.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Patients (PatientID, PatientName, DateOfBirth, Age, Sex)
            VALUES ($id, $name, $dob, $age, $sex);
            """;
        BindParameters(command, patient);
        command.ExecuteNonQuery();
    }

    public void Update(Patient patient)
    {
        using SqliteConnection connection = _context.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Patients
            SET PatientName = $name, DateOfBirth = $dob, Age = $age, Sex = $sex
            WHERE PatientID = $id;
            """;
        BindParameters(command, patient);
        command.ExecuteNonQuery();
    }

    public void Delete(string patientId)
    {
        using SqliteConnection connection = _context.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Patients WHERE PatientID = $id;";
        command.Parameters.AddWithValue("$id", patientId);
        command.ExecuteNonQuery();
    }

    private static void BindParameters(SqliteCommand command, Patient patient)
    {
        command.Parameters.AddWithValue("$id", patient.PatientID);
        command.Parameters.AddWithValue("$name", patient.PatientName);
        command.Parameters.AddWithValue("$dob", (object?)patient.DateOfBirth?.ToString("o") ?? DBNull.Value);
        command.Parameters.AddWithValue("$age", (object?)patient.Age ?? DBNull.Value);
        command.Parameters.AddWithValue("$sex", (object?)patient.Sex ?? DBNull.Value);
    }

    private static Patient Map(SqliteDataReader reader)
    {
        return new Patient
        {
            PatientID = reader.GetString(0),
            PatientName = reader.GetString(1),
            DateOfBirth = reader.IsDBNull(2) ? null : DateTime.Parse(reader.GetString(2)),
            Age = reader.IsDBNull(3) ? null : reader.GetInt32(3),
            Sex = reader.IsDBNull(4) ? null : reader.GetString(4)
        };
    }
}
