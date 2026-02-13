using Telegram.Bot;
using Telegram.Bot.Types;

namespace GordonWorker.Services;

public interface ITelegramService
{
    Task SendMessageAsync(string message);
    Task InstallWebhookAsync(string webhookUrl);
    Task<string> GetWebhookInfoAsync();
}

public class TelegramService : ITelegramService
{
    private readonly ISettingsService _settingsService;
    private readonly ILogger<TelegramService> _logger;

    public TelegramService(ISettingsService settingsService, ILogger<TelegramService> logger)
    {
        _settingsService = settingsService;
        _logger = logger;
    }

    public async Task<string> GetWebhookInfoAsync()
    {
        var settings = await _settingsService.GetSettingsAsync();
        if (string.IsNullOrWhiteSpace(settings.TelegramBotToken)) return "Token not configured.";

        var botClient = new TelegramBotClient(settings.TelegramBotToken);
        var info = await botClient.GetWebhookInfo();
        return System.Text.Json.JsonSerializer.Serialize(info, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    public async Task InstallWebhookAsync(string webhookUrl)
    {
        var settings = await _settingsService.GetSettingsAsync();
        if (string.IsNullOrWhiteSpace(settings.TelegramBotToken))
        {
            throw new Exception("Telegram Bot Token is not configured.");
        }

        var botClient = new TelegramBotClient(settings.TelegramBotToken);
        await botClient.SetWebhook(webhookUrl);
        _logger.LogInformation("Telegram webhook set to {Url}", webhookUrl);
    }

    public async Task SendMessageAsync(string message)
    {
        var settings = await _settingsService.GetSettingsAsync();

        if (string.IsNullOrWhiteSpace(settings.TelegramBotToken) || 
            string.IsNullOrWhiteSpace(settings.TelegramChatId))
        {
            _logger.LogWarning("Telegram settings are not fully configured. Cannot send message.");
            return;
        }

        try
        {
            var botClient = new TelegramBotClient(settings.TelegramBotToken);
            await botClient.SendMessage(
                chatId: settings.TelegramChatId,
                text: message,
                parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown
            );
            _logger.LogInformation("Telegram message sent to {ChatId}", settings.TelegramChatId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send Telegram message to {ChatId}", settings.TelegramChatId);
        }
    }
}
