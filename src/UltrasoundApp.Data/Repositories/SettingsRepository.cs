using Microsoft.Data.Sqlite;
using UltrasoundApp.Core.Models;
using UltrasoundApp.Core.Repositories;

namespace UltrasoundApp.Data.Repositories;

/// <summary>
/// ADO.NET (Microsoft.Data.Sqlite) repository for the single
/// <see cref="AppSettings"/> row, stored in the <c>Settings</c> table
/// (see <see cref="DbInitializer"/>) under the fixed primary key
/// <c>Id = 1</c> — the table's <c>CHECK (Id = 1)</c> constraint makes a
/// second row impossible at the database level, matching
/// <see cref="ISettingsRepository"/>'s "exactly one row" contract.
/// </summary>
public sealed class SettingsRepository : ISettingsRepository
{
    private readonly AppDbContext _context;

    public SettingsRepository(AppDbContext context)
    {
        _context = context;
    }

    public AppSettings? Get()
    {
        using SqliteConnection connection = _context.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT AeTitle, ListeningPort, ReportTemplatesFolderPath, ImageSheetBrandingTemplatePath,
                   PrinterName, LogoPath, HospitalName, Address, HeaderText, FooterText
            FROM Settings WHERE Id = 1;
            """;

        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public void Save(AppSettings settings)
    {
        using SqliteConnection connection = _context.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        // INSERT OR REPLACE against the fixed Id=1 primary key is a single
        // atomic upsert — inserts the row the first time Save is called,
        // overwrites it (all columns) every time after. No separate
        // Add/Update methods or "does a row exist yet" check needed.
        command.CommandText = """
            INSERT OR REPLACE INTO Settings
                (Id, AeTitle, ListeningPort, ReportTemplatesFolderPath, ImageSheetBrandingTemplatePath,
                 PrinterName, LogoPath, HospitalName, Address, HeaderText, FooterText)
            VALUES
                (1, $aeTitle, $listeningPort, $reportTemplatesFolderPath, $imageSheetBrandingTemplatePath,
                 $printerName, $logoPath, $hospitalName, $address, $headerText, $footerText);
            """;

        command.Parameters.AddWithValue("$aeTitle", settings.AeTitle);
        command.Parameters.AddWithValue("$listeningPort", settings.ListeningPort);
        command.Parameters.AddWithValue("$reportTemplatesFolderPath", (object?)settings.ReportTemplatesFolderPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$imageSheetBrandingTemplatePath", (object?)settings.ImageSheetBrandingTemplatePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$printerName", (object?)settings.PrinterName ?? DBNull.Value);
        command.Parameters.AddWithValue("$logoPath", (object?)settings.LogoPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$hospitalName", settings.HospitalName);
        command.Parameters.AddWithValue("$address", settings.Address);
        command.Parameters.AddWithValue("$headerText", (object?)settings.HeaderText ?? DBNull.Value);
        command.Parameters.AddWithValue("$footerText", (object?)settings.FooterText ?? DBNull.Value);

        command.ExecuteNonQuery();
    }

    private static AppSettings Map(SqliteDataReader reader) => new()
    {
        AeTitle = reader.GetString(0),
        ListeningPort = reader.GetInt32(1),
        ReportTemplatesFolderPath = reader.IsDBNull(2) ? null : reader.GetString(2),
        ImageSheetBrandingTemplatePath = reader.IsDBNull(3) ? null : reader.GetString(3),
        PrinterName = reader.IsDBNull(4) ? null : reader.GetString(4),
        LogoPath = reader.IsDBNull(5) ? null : reader.GetString(5),
        HospitalName = reader.GetString(6),
        Address = reader.GetString(7),
        HeaderText = reader.IsDBNull(8) ? null : reader.GetString(8),
        FooterText = reader.IsDBNull(9) ? null : reader.GetString(9),
    };
}
