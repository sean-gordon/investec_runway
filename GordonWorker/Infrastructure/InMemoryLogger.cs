using GordonWorker.Services;

namespace GordonWorker.Infrastructure;

public class InMemoryLogger : ILogger
{
    private readonly string _categoryName;
    private readonly ILogSinkService _sink;

    public InMemoryLogger(string categoryName, ILogSinkService sink)
    {
        _categoryName = categoryName;
        _sink = sink;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        if (exception != null)
        {
            message += $"\n{exception}";
        }
        
        _sink.AddLog(logLevel.ToString(), _categoryName, message);
    }
}
