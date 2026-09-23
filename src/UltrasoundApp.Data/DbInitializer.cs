using Microsoft.Data.Sqlite;

namespace UltrasoundApp.Data;

/// <summary>
/// Creates the SQLite database file and its schema on first run.
/// Safe to call every app startup — every statement is idempotent
/// (CREATE TABLE/INDEX IF NOT EXISTS), so it's a no-op once the schema
/// already matches.
/// </summary>
public static class DbInitializer
{
    /// <summary>
    /// Ensures the data folder and <c>app.db</c> exist and that all seven
    /// entity tables (with their foreign keys) are present.
    /// </summary>
    public static void Initialize(AppDbContext context)
    {
        string? dataDirectory = Path.GetDirectoryName(context.DatabasePath);
        if (!string.IsNullOrEmpty(dataDirectory) && !Directory.Exists(dataDirectory))
        {
            Directory.CreateDirectory(dataDirectory);
        }

        using SqliteConnection connection = context.CreateOpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction();

        foreach (string statement in SchemaStatements)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = statement;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static readonly string[] SchemaStatements =
    {
        // Patient — PatientID (DICOM ID) is the natural primary key.
        """
        CREATE TABLE IF NOT EXISTS Patients (
            PatientID   TEXT PRIMARY KEY NOT NULL,
            PatientName TEXT NOT NULL,
            DateOfBirth TEXT NULL,
            Age         INTEGER NULL,
            Sex         TEXT NULL
        );
        """,

        // Study — StudyInstanceUID is the natural primary key; FK to Patient.
        """
        CREATE TABLE IF NOT EXISTS Studies (
            StudyInstanceUID TEXT PRIMARY KEY NOT NULL,
            PatientID        TEXT NOT NULL,
            ExamType         TEXT NOT NULL,
            StudyDateTime    TEXT NOT NULL,
            Status           TEXT NOT NULL,
            FOREIGN KEY (PatientID) REFERENCES Patients (PatientID)
                ON DELETE RESTRICT
        );
        """,
        "CREATE INDEX IF NOT EXISTS IX_Studies_PatientID ON Studies (PatientID);",

        // Image — SOPInstanceUID is the natural primary key; FK to Study.
        """
        CREATE TABLE IF NOT EXISTS Images (
            SOPInstanceUID   TEXT PRIMARY KEY NOT NULL,
            StudyInstanceUID TEXT NOT NULL,
            FilePath         TEXT NOT NULL,
            ThumbnailPath    TEXT NULL,
            FOREIGN KEY (StudyInstanceUID) REFERENCES Studies (StudyInstanceUID)
                ON DELETE CASCADE
        );
        """,
        "CREATE INDEX IF NOT EXISTS IX_Images_StudyInstanceUID ON Images (StudyInstanceUID);",

        // ReportTemplate — one template per exam type, so ExamType is the primary key.
        // PlaceholderFields (List<string>) is stored as a JSON array column.
        """
        CREATE TABLE IF NOT EXISTS ReportTemplates (
            ExamType             TEXT PRIMARY KEY NOT NULL,
            HtmlTemplateFilePath TEXT NOT NULL,
            PlaceholderFieldsJson TEXT NOT NULL DEFAULT '[]'
        );
        """,

        // GeneratedReport — one generated/edited report per study, so
        // StudyInstanceUID is both the primary key and the FK to Study.
        """
        CREATE TABLE IF NOT EXISTS GeneratedReports (
            StudyInstanceUID TEXT PRIMARY KEY NOT NULL,
            FieldValuesJson  TEXT NOT NULL DEFAULT '{}',
            GeneratedPdfPath TEXT NULL,
            CreatedAt        TEXT NOT NULL,
            ModifiedAt       TEXT NOT NULL,
            FOREIGN KEY (StudyInstanceUID) REFERENCES Studies (StudyInstanceUID)
                ON DELETE CASCADE
        );
        """,

        // PrintJob — an append-only log of print operations; a study can
        // have many, and no single column is naturally unique, so it relies
        // on SQLite's implicit rowid rather than an extra surrogate column.
        """
        CREATE TABLE IF NOT EXISTS PrintJobs (
            Type             TEXT NOT NULL,
            StudyInstanceUID TEXT NOT NULL,
            Timestamp        TEXT NOT NULL,
            PrinterUsed      TEXT NOT NULL,
            FOREIGN KEY (StudyInstanceUID) REFERENCES Studies (StudyInstanceUID)
                ON DELETE CASCADE
        );
        """,
        "CREATE INDEX IF NOT EXISTS IX_PrintJobs_StudyInstanceUID ON PrintJobs (StudyInstanceUID);",

        // Settings — exactly one row, always Id=1 (enforced by the CHECK
        // constraint below, not just convention). SettingsRepository.Save
        // upserts this single row with INSERT OR REPLACE rather than
        // needing separate Add/Update methods.
        """
        CREATE TABLE IF NOT EXISTS Settings (
            Id                             INTEGER PRIMARY KEY NOT NULL CHECK (Id = 1),
            AeTitle                        TEXT NOT NULL,
            ListeningPort                  INTEGER NOT NULL,
            ReportTemplatesFolderPath      TEXT NULL,
            ImageSheetBrandingTemplatePath TEXT NULL,
            PrinterName                    TEXT NULL,
            LogoPath                       TEXT NULL,
            HospitalName                   TEXT NOT NULL,
            Address                        TEXT NOT NULL,
            HeaderText                     TEXT NULL,
            FooterText                     TEXT NULL
        );
        """
    };
}
