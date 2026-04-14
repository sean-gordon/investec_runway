using System.Collections.Concurrent;

namespace GordonWorker.Services;

public record LogEntry(DateTime Timestamp, string Level, string Category, string Message);

public interface ILogSinkService
{
    void AddLog(string level, string category, string message);
    IEnumerable<LogEntry> GetLogs();
}

/// <summary>
/// Dual-sink logger: keeps the last 1000 entries in memory (for the Logs tab)
/// AND writes every entry to daily rotating log files under /app/logs.
///
/// File structure:
///   /app/logs/info/    gordon-YYYY-MM-DD.log   (Information + Warning + higher)
///   /app/logs/error/   gordon-YYYY-MM-DD.log   (Error + Critical only)
///   /app/logs/debug/   gordon-YYYY-MM-DD.log   (Debug + Trace — all levels)
/// </summary>
public class LogSinkService : ILogSinkService
{
    private readonly ConcurrentQueue<LogEntry> _logs = new();
    private const int MaxLogs = 1000;

    // Determine base log directory: /app/logs inside Docker, ./logs elsewhere
    private static readonly string LogRoot = Path.Combine(
        AppContext.BaseDirectory.Contains("/app") ? "/app" : AppContext.BaseDirectory,
        "logs");

    private static readonly string InfoDir  = Path.Combine(LogRoot, "info");
    private static readonly string ErrorDir = Path.Combine(LogRoot, "error");
    private static readonly string DebugDir = Path.Combine(LogRoot, "debug");

    // Lock per directory to avoid concurrent write collisions
    private static readonly object _infoLock  = new();
    private static readonly object _errorLock = new();
    private static readonly object _debugLock = new();

    static LogSinkService()
    {
        // Ensure directories exist at startup
        Directory.CreateDirectory(InfoDir);
        Directory.CreateDirectory(ErrorDir);
        Directory.CreateDirectory(DebugDir);
    }

    public void AddLog(string level, string category, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, category, message);

        // --- In-memory ring buffer (Logs tab) ---
        _logs.Enqueue(entry);
        while (_logs.Count > MaxLogs)
            _logs.TryDequeue(out _);

        // --- File persistence ---
        var line = $"[{entry.Timestamp:yyyy-MM-dd HH:mm:ss}] [{level.ToUpper(),-11}] [{ShortCategory(category)}] {message}";
        var date = entry.Timestamp.ToString("yyyy-MM-dd");

        // Debug file: everything
        WriteToFile(Path.Combine(DebugDir, $"gordon-{date}.log"), line, _debugLock);

        // Info file: Information, Warning, Error, Critical (skip Debug/Trace noise)
        var lvl = level.ToUpperInvariant();
        if (lvl is "INFORMATION" or "WARNING" or "ERROR" or "CRITICAL")
            WriteToFile(Path.Combine(InfoDir, $"gordon-{date}.log"), line, _infoLock);

        // Error file: Error and Critical only
        if (lvl is "ERROR" or "CRITICAL")
            WriteToFile(Path.Combine(ErrorDir, $"gordon-{date}.log"), line, _errorLock);
    }

    public IEnumerable<LogEntry> GetLogs()
    {
        return _logs.ToArray().OrderByDescending(l => l.Timestamp);
    }

    private static string ShortCategory(string category)
    {
        // Trim full namespace to just the class name for readability
        var parts = category.Split('.');
        return parts.Length > 1 ? parts[^1] : category;
    }

    private static void WriteToFile(string path, string line, object fileLock)
    {
        try
        {
            lock (fileLock)
            {
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch
        {
            // Never let file I/O errors crash the application
        }
    }
}
