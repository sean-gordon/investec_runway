using Telegram.Bot;
using Telegram.Bot.Types;

namespace GordonWorker.Services;

public interface ITelegramService
{
    Task SendMessageAsync(int userId, string message, string? targetChatId = null);
    Task<int> SendMessageWithIdAsync(int userId, string message, string? targetChatId = null);
    Task EditMessageAsync(int userId, int messageId, string newMessage, string? targetChatId = null);
    Task InstallWebhookAsync(int userId, string webhookUrl);
    Task<string> GetWebhookInfoAsync(int userId);
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

    public async Task<string> GetWebhookInfoAsync(int userId)
    {
        var settings = await _settingsService.GetSettingsAsync(userId);
        if (string.IsNullOrWhiteSpace(settings.TelegramBotToken)) return "Token not configured.";

        var botClient = new TelegramBotClient(settings.TelegramBotToken);
        var info = await botClient.GetWebhookInfo();
        return System.Text.Json.JsonSerializer.Serialize(info, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    public async Task InstallWebhookAsync(int userId, string webhookUrl)
    {
        var settings = await _settingsService.GetSettingsAsync(userId);
        if (string.IsNullOrWhiteSpace(settings.TelegramBotToken))
        {
            throw new Exception("Telegram Bot Token is not configured.");
        }

        var botClient = new TelegramBotClient(settings.TelegramBotToken);
        await botClient.SetWebhook(webhookUrl);
        _logger.LogInformation("Telegram webhook set to {Url} for user {UserId}", webhookUrl, userId);
    }

    public async Task SendMessageAsync(int userId, string message, string? targetChatId = null)
    {
        await SendMessageWithIdAsync(userId, message, targetChatId);
    }

    public async Task<int> SendMessageWithIdAsync(int userId, string message, string? targetChatId = null)
    {
        var settings = await _settingsService.GetSettingsAsync(userId);
        var chatId = targetChatId ?? settings.TelegramChatId;

        if (string.IsNullOrWhiteSpace(settings.TelegramBotToken) || 
            string.IsNullOrWhiteSpace(chatId))
        {
            _logger.LogWarning("Telegram settings (Token or ChatId) are not fully configured for user {UserId}. Cannot send message.", userId);
            return 0;
        }

        try
        {
            var botClient = new TelegramBotClient(settings.TelegramBotToken);
            var sentMsg = await botClient.SendMessage(
                chatId: chatId,
                text: message,
                parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown
            );
            _logger.LogInformation("Telegram message sent to {ChatId} for user {UserId}", chatId, userId);
            return sentMsg.MessageId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send Telegram message to {ChatId} for user {UserId}", chatId, userId);
            return 0;
        }
    }

    public async Task EditMessageAsync(int userId, int messageId, string newMessage, string? targetChatId = null)
    {
        var settings = await _settingsService.GetSettingsAsync(userId);
        var chatId = targetChatId ?? settings.TelegramChatId;

        if (string.IsNullOrWhiteSpace(settings.TelegramBotToken) || string.IsNullOrWhiteSpace(chatId)) return;

        try
        {
            var botClient = new TelegramBotClient(settings.TelegramBotToken);
            await botClient.EditMessageText(
                chatId: chatId,
                messageId: messageId,
                text: newMessage,
                parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown
            );
            _logger.LogInformation("Telegram message {MsgId} edited for user {UserId}", messageId, userId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to edit Telegram message {MsgId}", messageId);
        }
    }
}
