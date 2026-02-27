using System.Collections.Concurrent;
using Telegram.Bot;

namespace GordonWorker.Services;

public interface ITelegramBotClientFactory
{
    ITelegramBotClient GetClient(string botToken);
}

public class TelegramBotClientFactory : ITelegramBotClientFactory
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ConcurrentDictionary<string, ITelegramBotClient> _clients = new();

    public TelegramBotClientFactory(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public ITelegramBotClient GetClient(string botToken)
    {
        if (string.IsNullOrWhiteSpace(botToken))
            throw new ArgumentException("Bot token cannot be null or empty", nameof(botToken));

        return _clients.GetOrAdd(botToken, token =>
        {
            var httpClient = _httpClientFactory.CreateClient("TelegramBotClient");
            return new TelegramBotClient(token, httpClient);
        });
    }
}
