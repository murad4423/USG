using System.Text;

namespace UltrasoundApp.Core.Logging;

/// <summary>Severity of a single <see cref="AppLog"/> entry.</summary>
public enum LogLevel
{
    Info,
    Warning,
    Error
}

/// <summary>
/// Minimal rotating file logger writing to <c>data/logs/app-YYYY-MM-DD.log</c>.
///
/// <para>
/// <b>Why hand-rolled rather than Serilog/NLog:</b> this app needs exactly
/// one sink (a daily file), has no structured-logging or log-shipping
/// requirements, and runs on clinic machines where every extra NuGet
/// dependency is another thing to license, update and explain. The whole
/// implementation is ~80 lines; a logging framework would be more
/// configuration surface than code saved. Revisit if log shipping or
/// per-category filtering is ever needed.
/// </para>
///
/// <para>
/// <b>Rotation</b> is by calendar day (a new file each day, named from the
/// local date) plus a retention sweep that deletes files older than
/// <see cref="RetentionDays"/>. The sweep runs once per process on first
/// write, not on a timer — this app is opened and closed daily, so that's
/// frequent enough and avoids a background thread.
/// </para>
///
/// <para>
/// <b>Thread safety and failure policy:</b> writes are serialized through a
/// lock, and every write is wrapped so that a logging failure (locked file,
/// full disk, missing permissions) can never propagate into the caller.
/// Logging must never be the reason a print or a DICOM receive fails — a
/// swallowed log line is strictly better than a crashed clinical workflow.
/// </para>
///
/// <para>
/// This lives in Core (which has no project references) so Dicom, Printing,
/// Templating, Data and Host can all log through one implementation. It
/// therefore resolves the data folder itself rather than depending on
/// <c>UltrasoundApp.Data.AppPaths</c> — see <see cref="GetLogDirectory"/>.
/// </para>
/// </summary>
public static class AppLog
{
    /// <summary>Log files older than this many days are deleted on the first write of each process.</summary>
    public const int RetentionDays = 30;

    private const string SolutionFileName = "UltrasoundApp.sln";

    private static readonly object WriteLock = new();
    private static bool _retentionSweepDone;

    /// <summary>
    /// Optional extra sink, invoked with the same formatted line that goes
    /// to the file. Used by the console manual-test harnesses (and
    /// DicomScpListener's existing console logging) so a single call both
    /// persists to the log file and stays visible in an attached terminal.
    /// </summary>
    public static Action<string>? EchoSink { get; set; }

    /// <summary>Full path to <c>data/logs</c>, created if it doesn't exist yet.</summary>
    public static string GetLogDirectory()
    {
        string root = FindRepoRoot() ?? AppContext.BaseDirectory;
        string logDirectory = Path.Combine(root, "data", "logs");
        Directory.CreateDirectory(logDirectory);
        return logDirectory;
    }

    /// <summary>Full path to today's log file (<c>data/logs/app-YYYY-MM-DD.log</c>).</summary>
    public static string GetCurrentLogFilePath()
    {
        return Path.Combine(GetLogDirectory(), $"app-{DateTime.Now:yyyy-MM-dd}.log");
    }

    /// <summary>Logs a routine event — a DICOM instance received, a print job sent.</summary>
    public static void Info(string category, string message) => Write(LogLevel.Info, category, message, exception: null);

    /// <summary>Logs a recoverable problem — a skipped image, a fallback that kicked in.</summary>
    public static void Warning(string category, string message) => Write(LogLevel.Warning, category, message, exception: null);

    /// <summary>Logs a failure, optionally with the exception whose type/message/stack trace should be recorded.</summary>
    public static void Error(string category, string message, Exception? exception = null) =>
        Write(LogLevel.Error, category, message, exception);

    /// <summary>
    /// Formats and appends one entry. Never throws: see the class remarks
    /// on why a logging failure must not surface to the caller.
    /// </summary>
    public static void Write(LogLevel level, string category, string message, Exception? exception)
    {
        var builder = new StringBuilder();
        builder.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
            .Append(" [").Append(level.ToString().ToUpperInvariant()).Append(']')
            .Append(" [").Append(category).Append("] ")
            .Append(message);

        if (exception is not null)
        {
            builder.AppendLine()
                .Append("    ").Append(exception.GetType().FullName).Append(": ").Append(exception.Message);

            if (!string.IsNullOrWhiteSpace(exception.StackTrace))
            {
                builder.AppendLine().Append(exception.StackTrace);
            }

            // Inner exceptions carry the actual cause often enough
            // (TypeInitializationException, AggregateException, the
            // IOException under a failed File.Copy) to be worth unwinding.
            Exception? inner = exception.InnerException;
            while (inner is not null)
            {
                builder.AppendLine()
                    .Append("    --- inner: ").Append(inner.GetType().FullName).Append(": ").Append(inner.Message);
                inner = inner.InnerException;
            }
        }

        string line = builder.ToString();

        try
        {
            EchoSink?.Invoke(line);
        }
        catch
        {
            // An echo sink that throws must not take down the file write below.
        }

        try
        {
            lock (WriteLock)
            {
                string logFilePath = GetCurrentLogFilePath();
                File.AppendAllText(logFilePath, line + Environment.NewLine, Encoding.UTF8);

                if (!_retentionSweepDone)
                {
                    _retentionSweepDone = true;
                    SweepOldLogs();
                }
            }
        }
        catch
        {
            // Deliberately swallowed — see the class remarks. There is no
            // meaningful fallback: the thing we would report the failure
            // with is the thing that just failed.
        }
    }

    /// <summary>Deletes log files older than <see cref="RetentionDays"/>. Called under <see cref="WriteLock"/>.</summary>
    private static void SweepOldLogs()
    {
        try
        {
            DateTime cutoff = DateTime.Now.Date.AddDays(-RetentionDays);

            foreach (string path in Directory.EnumerateFiles(GetLogDirectory(), "app-*.log"))
            {
                // Go by the date in the filename rather than the file's
                // LastWriteTime: copying or restoring log files would reset
                // the timestamp and silently change what gets retained.
                string name = Path.GetFileNameWithoutExtension(path);
                if (name.Length != "app-yyyy-MM-dd".Length)
                {
                    continue;
                }

                if (DateTime.TryParse(name["app-".Length..], out DateTime fileDate) && fileDate.Date < cutoff)
                {
                    File.Delete(path);
                }
            }
        }
        catch
        {
            // Retention is housekeeping; failing to prune must not fail the write.
        }
    }

    private static string? FindRepoRoot()
    {
        // Mirrors UltrasoundApp.Data.AppPaths.FindRepoRoot — duplicated
        // rather than shared because Core has no project references (and
        // Data references Core, not the reverse). Keep the two in sync.
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
