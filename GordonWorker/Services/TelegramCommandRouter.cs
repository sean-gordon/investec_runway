using Dapper;
using GordonWorker.Models;
using GordonWorker.Repositories;
using Npgsql;
using System.Text.Json;

namespace GordonWorker.Services;

public class TelegramCommandRouter(
    IServiceProvider serviceProvider,
    ITransactionRepository transactionRepository,
    ITelegramService telegramService,
    IAiService aiService,
    ISettingsService settingsService,
    ILogger<TelegramCommandRouter> logger) : ITelegramCommandRouter
{
    public async Task<string> RouteCommandAsync(int userId, string messageText, AppSettings settings, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(messageText) || !messageText.StartsWith("/"))
            return null;

        var cmd = messageText.Split(' ')[0].ToLower();
        logger.LogDebug("Routing command '{Cmd}' for user {UserId}", cmd, userId);

        if (cmd == "/clear")
        {
            await telegramService.SendMessageWithButtonsAsync(userId,
                "⚠️ <b>Warning: Clear History</b>\n\nThis will permanently delete your entire conversation history with the AI. This action cannot be undone.\n\nAre you sure?",
                new List<(string Text, string CallbackData)> { ("Yes, Clear It", "/clear_confirmed"), ("No, Cancel", "/cancel") },
                settings.TelegramChatId);
            return "Command Handled";
        }

        if (cmd == "/clear_confirmed")
        {
            try
            {
                await transactionRepository.DeleteChatHistoryAsync(userId);
                await telegramService.SendMessageAsync(userId, "✅ <b>Success:</b> Your conversation history has been cleared.", settings.TelegramChatId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to clear chat history for user {UserId}", userId);
                await telegramService.SendMessageAsync(userId, "❌ <b>Error:</b> Failed to clear history. Please try again.", settings.TelegramChatId);
            }
            return "Command Handled";
        }

        if (cmd == "/cancel")
        {
            await telegramService.SendMessageAsync(userId, "Action cancelled.", settings.TelegramChatId);
            return "Command Handled";
        }

        if (cmd == "/model")
        {
            await telegramService.SendMessageWithButtonsAsync(userId,
                "⚙️ <b>AI Model Configuration</b>\n\nWhich provider would you like to configure?",
                new List<(string Text, string CallbackData)> { ("Primary AI", "/model_provider_primary"), ("Backup AI", "/model_provider_backup") },
                settings.TelegramChatId);
            return "Command Handled";
        }

        if (cmd == "/model_provider_primary" || cmd == "/model_provider_backup")
        {
            try
            {
                bool isPrimary = cmd.EndsWith("primary");
                var provider = isPrimary ? settings.AiProvider : settings.FallbackAiProvider;
                await telegramService.SendMessageWithButtonsAsync(userId,
                    $"⚙️ <b>Select Provider for {(isPrimary ? "Primary" : "Backup")}</b>\n\nCurrent: <i>{provider}</i>",
                    new List<(string Text, string CallbackData)> {
                        ("Ollama", $"/model_select_{ (isPrimary ? "p" : "b") }_ollama"),
                        ("Gemini", $"/model_select_{ (isPrimary ? "p" : "b") }_gemini")
                    },
                    settings.TelegramChatId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to show provider selection for user {UserId}", userId);
                await telegramService.SendMessageAsync(userId, "❌ <b>Error:</b> Failed to load provider selection.", settings.TelegramChatId);
            }
            return "Command Handled";
        }

        if (cmd.StartsWith("/model_select_"))
        {
            try
            {
                var parts = cmd.Split('_');
                if (parts.Length < 5) return "Invalid Command";

                bool isPrimary = parts[3] == "p";
                var providerName = parts[4];

                string currentModel;
                if (isPrimary)
                    currentModel = settings.AiProvider == "Gemini" ? settings.GeminiModelName : settings.OllamaModelName;
                else
                    currentModel = settings.FallbackAiProvider == "Gemini" ? settings.FallbackGeminiModelName : settings.FallbackOllamaModelName;

                var tempSettings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
                if (isPrimary) tempSettings.AiProvider = providerName == "ollama" ? "Ollama" : "Gemini";
                else tempSettings.FallbackAiProvider = providerName == "ollama" ? "Ollama" : "Gemini";

                var models = await aiService.GetAvailableModelsAsync(userId, !isPrimary, false, tempSettings);
                var buttons = models.Take(8).Select(m => (m, $"/model_set_{ (isPrimary ? "p" : "b") }_{providerName}_{m}")).ToList();
                buttons.Add(("Cancel", "/cancel"));

                await telegramService.SendMessageWithButtonsAsync(userId,
                    $"⚙️ <b>Select Model ({(isPrimary ? "Primary" : "Backup")})</b>\n\nProvider: {providerName.ToUpper()}\nCurrent: {currentModel}",
                    buttons,
                    settings.TelegramChatId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to show model selection for user {UserId}", userId);
                await telegramService.SendMessageAsync(userId, "❌ <b>Error:</b> Failed to load model selection.", settings.TelegramChatId);
            }
            return "Command Handled";
        }

        if (cmd.StartsWith("/model_set_"))
        {
            try
            {
                var parts = cmd.Split('_');
                if (parts.Length < 6) return "Invalid Command";

                bool isPrimary = parts[3] == "p";
                var provider = parts[4];
                var modelName = string.Join("_", parts.Skip(5));

                var current = await settingsService.GetSettingsAsync(userId);
                if (isPrimary)
                {
                    current.AiProvider = provider == "ollama" ? "Ollama" : "Gemini";
                    if (provider == "ollama") current.OllamaModelName = modelName;
                    else current.GeminiModelName = modelName;
                }
                else
                {
                    current.FallbackAiProvider = provider == "ollama" ? "Ollama" : "Gemini";
                    if (provider == "ollama") current.FallbackOllamaModelName = modelName;
                    else current.FallbackGeminiModelName = modelName;
                }

                await settingsService.UpdateSettingsAsync(userId, current);
                await telegramService.SendMessageAsync(userId, $"✅ <b>Success:</b> {(isPrimary ? "Primary" : "Backup")} AI updated to <b>{modelName}</b> ({provider.ToUpper()}).", settings.TelegramChatId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to set model for user {UserId}", userId);
                await telegramService.SendMessageAsync(userId, "❌ <b>Error:</b> Failed to update model settings.", settings.TelegramChatId);
            }
            return "Command Handled";
        }

        return null; // Not a slash command or unknown
    }
}
