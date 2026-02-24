using GordonWorker.Services;
using Microsoft.Extensions.Logging;

namespace GordonWorker.Infrastructure;

public class InMemoryLoggerProvider : ILoggerProvider
{
    private readonly ILogSinkService _sink;

    public InMemoryLoggerProvider(ILogSinkService sink)
    {
        _sink = sink;
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new InMemoryLogger(categoryName, _sink);
    }

    public void Dispose()
    {
    }
}
