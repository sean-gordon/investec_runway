using GordonWorker.Models;

namespace GordonWorker.Services;

public interface ITelegramCommandRouter
{
    Task<string> RouteCommandAsync(int userId, string messageText, AppSettings settings, CancellationToken ct);
}
