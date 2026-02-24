using System.Collections.Concurrent;

namespace GordonWorker.Services;

public record LogEntry(DateTime Timestamp, string Level, string Category, string Message);

public interface ILogSinkService
{
    void AddLog(string level, string category, string message);
    IEnumerable<LogEntry> GetLogs();
}

public class LogSinkService : ILogSinkService
{
    private readonly ConcurrentQueue<LogEntry> _logs = new();
    private const int MaxLogs = 1000;

    public void AddLog(string level, string category, string message)
    {
        _logs.Enqueue(new LogEntry(DateTime.Now, level, category, message));
        
        while (_logs.Count > MaxLogs)
        {
            _logs.TryDequeue(out _);
        }
    }

    public IEnumerable<LogEntry> GetLogs()
    {
        return _logs.ToArray().OrderByDescending(l => l.Timestamp);
    }
}
