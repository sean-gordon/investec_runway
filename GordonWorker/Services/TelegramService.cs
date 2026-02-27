using Telegram.Bot;
using Telegram.Bot.Types;
using System.Net;

namespace GordonWorker.Services;

public interface ITelegramService
{
    Task SendMessageAsync(int userId, string message, string? targetChatId = null);
    Task<int> SendMessageWithIdAsync(int userId, string message, string? targetChatId = null);
    Task EditMessageAsync(int userId, int messageId, string newMessage, string? targetChatId = null);
    Task SendImageAsync(int userId, byte[] imageData, string caption, string? targetChatId = null);
    Task SendMessageWithButtonsAsync(int userId, string message, List<(string Text, string CallbackData)> buttons, string? targetChatId = null);
    Task InstallWebhookAsync(int userId, string webhookUrl);
    Task<string> GetWebhookInfoAsync(int userId);
}

public class TelegramService : ITelegramService
{
    private readonly ISettingsService _settingsService;
    private readonly ITelegramBotClientFactory _botClientFactory;
    private readonly ILogger<TelegramService> _logger;

    public TelegramService(ISettingsService settingsService, ITelegramBotClientFactory botClientFactory, ILogger<TelegramService> logger)
    {
        _settingsService = settingsService;
        _botClientFactory = botClientFactory;
        _logger = logger;
    }

    public async Task SendMessageWithButtonsAsync(int userId, string message, List<(string Text, string CallbackData)> buttons, string? targetChatId = null)
    {
        var settings = await _settingsService.GetSettingsAsync(userId);
        var chatId = targetChatId ?? settings.TelegramChatId;

        if (string.IsNullOrWhiteSpace(settings.TelegramBotToken) || string.IsNullOrWhiteSpace(chatId)) return;

        try
        {
            var botClient = _botClientFactory.GetClient(settings.TelegramBotToken);
            var keyboard = new Telegram.Bot.Types.ReplyMarkups.InlineKeyboardMarkup(
                buttons.Select(b => Telegram.Bot.Types.ReplyMarkups.InlineKeyboardButton.WithCallbackData(b.Text, b.CallbackData))
            );

            await botClient.SendMessage(
                chatId: chatId,
                text: message,
                parseMode: Telegram.Bot.Types.Enums.ParseMode.Html,
                replyMarkup: keyboard
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send Telegram message with buttons.");
        }
    }

    public async Task<string> GetWebhookInfoAsync(int userId)
    {
        var settings = await _settingsService.GetSettingsAsync(userId);
        if (string.IsNullOrWhiteSpace(settings.TelegramBotToken)) return "Token not configured.";

        var botClient = _botClientFactory.GetClient(settings.TelegramBotToken);
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

        var botClient = _botClientFactory.GetClient(settings.TelegramBotToken);
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

        if (string.IsNullOrWhiteSpace(settings.TelegramBotToken) || string.IsNullOrWhiteSpace(chatId))
        {
            _logger.LogWarning("Telegram settings incomplete for user {UserId}.", userId);
            return 0;
        }

        try
        {
            var botClient = _botClientFactory.GetClient(settings.TelegramBotToken);
            var sentMsg = await botClient.SendMessage(
                chatId: chatId,
                text: message,
                parseMode: Telegram.Bot.Types.Enums.ParseMode.Html
            );
            _logger.LogInformation("Telegram message {MsgId} delivered to {ChatId}", sentMsg.MessageId, chatId);
            return sentMsg.MessageId;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send HTML Telegram message to {ChatId}. Retrying as plain text.", chatId);
            try
            {
                var botClient = _botClientFactory.GetClient(settings.TelegramBotToken);
                var sentMsg = await botClient.SendMessage(
                    chatId: chatId,
                    text: message // No parse mode = plain text
                );
                return sentMsg.MessageId;
            }
            catch (Exception ex2)
            {
                _logger.LogError(ex2, "Failed to send plain text Telegram message to {ChatId}", chatId);
                return 0;
            }
        }
    }

    public async Task EditMessageAsync(int userId, int messageId, string newMessage, string? targetChatId = null)
    {
        var settings = await _settingsService.GetSettingsAsync(userId);
        var chatId = targetChatId ?? settings.TelegramChatId;

        if (string.IsNullOrWhiteSpace(settings.TelegramBotToken) || string.IsNullOrWhiteSpace(chatId)) return;

        try
        {
            var botClient = _botClientFactory.GetClient(settings.TelegramBotToken);
            await botClient.EditMessageText(
                chatId: chatId,
                messageId: messageId,
                text: newMessage,
                parseMode: Telegram.Bot.Types.Enums.ParseMode.Html
            );
            _logger.LogInformation("Telegram message {MsgId} edited", messageId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to edit HTML Telegram message {MsgId}. Retrying as plain text.", messageId);
            try
            {
                var botClient = _botClientFactory.GetClient(settings.TelegramBotToken);
                await botClient.EditMessageText(
                    chatId: chatId,
                    messageId: messageId,
                    text: newMessage
                );
            }
            catch (Exception ex2)
            {
                _logger.LogError(ex2, "Failed to edit plain text Telegram message {MsgId}", messageId);
            }
        }
    }

    public async Task SendImageAsync(int userId, byte[] imageData, string caption, string? targetChatId = null)
    {
        var settings = await _settingsService.GetSettingsAsync(userId);
        var chatId = targetChatId ?? settings.TelegramChatId;

        if (string.IsNullOrWhiteSpace(settings.TelegramBotToken) || string.IsNullOrWhiteSpace(chatId)) return;

        try
        {
            var botClient = _botClientFactory.GetClient(settings.TelegramBotToken);
            using var stream = new MemoryStream(imageData);
            await botClient.SendPhoto(
                chatId: chatId,
                photo: InputFile.FromStream(stream),
                caption: caption,
                parseMode: Telegram.Bot.Types.Enums.ParseMode.Html
            );
            _logger.LogInformation("Telegram image sent to {ChatId}", chatId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send Telegram image.");
        }
    }

    public static string EscapeHtml(string? text)
    {
        // Now using HTML, so we use HtmlEncode
        if (string.IsNullOrEmpty(text)) return "";
        return WebUtility.HtmlEncode(text);
    }
}
