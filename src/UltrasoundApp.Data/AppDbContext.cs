using Microsoft.Data.Sqlite;

namespace UltrasoundApp.Data;

/// <summary>
/// Owns the SQLite connection string/location for the app and hands out
/// opened, foreign-key-enforcing connections.
///
/// This is deliberately NOT an EF Core DbContext — the data layer uses
/// Microsoft.Data.Sqlite directly (plain ADO.NET) across every repository,
/// so this class is just the shared connection factory they all use.
/// </summary>
public sealed class AppDbContext
{
    /// <summary>Full path to the SQLite database file on disk.</summary>
    public string DatabasePath { get; }

    /// <summary>Connection string built from <see cref="DatabasePath"/>.</summary>
    public string ConnectionString { get; }

    public AppDbContext()
        : this(AppPaths.GetDatabasePath())
    {
    }

    /// <param name="databasePath">
    /// Explicit path to the database file. Mainly useful for tests; normal
    /// app startup should use the parameterless constructor.
    /// </param>
    public AppDbContext(string databasePath)
    {
        DatabasePath = databasePath;
        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath
        }.ToString();
    }

    /// <summary>
    /// Opens a new connection to the database with foreign key enforcement
    /// turned on. Callers are responsible for disposing it (e.g. via
    /// <c>using</c>).
    /// </summary>
    public SqliteConnection CreateOpenConnection()
    {
        var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        using var pragmaCommand = connection.CreateCommand();
        pragmaCommand.CommandText = "PRAGMA foreign_keys = ON;";
        pragmaCommand.ExecuteNonQuery();

        return connection;
    }
}
