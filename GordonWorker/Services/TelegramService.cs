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
    /// <summary>
    /// Sends a placeholder message and progressively edits it as text chunks arrive from
    /// the AI. Use for long-form replies (briefings, affordability verdicts, free-form QA)
    /// so the user sees something within ~1s instead of staring at "typing…" for 30s.
    /// Returns the final accumulated text.
    /// </summary>
    Task<string> StreamMessageAsync(int userId, IAsyncEnumerable<string> chunks, string? header = null, string? targetChatId = null, CancellationToken ct = default);
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

    public async Task<string> StreamMessageAsync(int userId, IAsyncEnumerable<string> chunks, string? header = null, string? targetChatId = null, CancellationToken ct = default)
    {
        // Telegram limits Bot API edit calls to ~1/sec per chat — we use a 1.2s minimum gap
        // and only edit when there's actually new text. The final flush happens unconditionally
        // after the stream completes so the user always sees the full reply.
        var headerLine = string.IsNullOrWhiteSpace(header) ? "" : header + "\n\n";
        var placeholder = headerLine + "⏳ <i>Thinking…</i>";
        var messageId = await SendMessageWithIdAsync(userId, placeholder, targetChatId);
        if (messageId == 0)
        {
            // Send failed entirely — just buffer the stream and bail. The caller can decide
            // what to do with the result; we don't want to silently swallow the AI's work.
            var sb = new System.Text.StringBuilder();
            await foreach (var c in chunks.WithCancellation(ct)) sb.Append(c);
            return sb.ToString();
        }

        var buffer = new System.Text.StringBuilder();
        var lastEdit = System.Diagnostics.Stopwatch.StartNew();
        var minEditGap = TimeSpan.FromMilliseconds(1200);
        string lastSent = placeholder;

        try
        {
            await foreach (var chunk in chunks.WithCancellation(ct))
            {
                if (string.IsNullOrEmpty(chunk)) continue;
                buffer.Append(chunk);

                if (lastEdit.Elapsed >= minEditGap)
                {
                    var current = headerLine + System.Net.WebUtility.HtmlEncode(buffer.ToString());
                    if (current != lastSent)
                    {
                        await EditMessageAsync(userId, messageId, current, targetChatId);
                        lastSent = current;
                        lastEdit.Restart();
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Budget hit mid-stream. Show whatever we got plus a tail marker so the user
            // knows it was truncated, not just stalled.
            var truncated = headerLine + System.Net.WebUtility.HtmlEncode(buffer.ToString()) + "\n\n<i>(stopped early — try again for the full answer)</i>";
            try { await EditMessageAsync(userId, messageId, truncated, targetChatId); } catch { /* best-effort */ }
            return buffer.ToString();
        }

        // Final flush: emit the complete buffer in one last edit, this time without HTML
        // encoding so the AI's intentional <b>/<i> markup actually renders. During streaming
        // we encode to avoid Telegram rejecting partial/unclosed tags mid-flight.
        var finalText = buffer.Length == 0
            ? headerLine + "<i>(no response)</i>"
            : headerLine + buffer.ToString();
        await EditMessageAsync(userId, messageId, finalText, targetChatId);
        return buffer.ToString();
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
