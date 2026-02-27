using Dapper;
using GordonWorker.Models;
using Npgsql;
using System.Text.Json;
using System.Threading.Channels;
using Telegram.Bot;
using Microsoft.Extensions.Caching.Memory;

namespace GordonWorker.Services;

public interface ITelegramChatService
{
    Task EnqueueMessageAsync(int userId, string chatId, string messageText);
}

public class TelegramChatService : BackgroundService, ITelegramChatService
{
    private readonly Channel<TelegramRequest> _queue;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<TelegramChatService> _logger;

    public TelegramChatService(IServiceProvider serviceProvider, ILogger<TelegramChatService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _queue = Channel.CreateUnbounded<TelegramRequest>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false
        });
    }

    public async Task EnqueueMessageAsync(int userId, string chatId, string messageText)
    {
        await _queue.Writer.WriteAsync(new TelegramRequest(userId, chatId, messageText));
        _logger.LogInformation("Telegram request enqueued for user {UserId}", userId);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Telegram Chat Service started.");

        await foreach (var request in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await ProcessMessageAsync(request, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Fatal error processing Telegram message for user {UserId}", request.UserId);
                }
            }, stoppingToken);
        }
    }

    private async Task ProcessMessageAsync(TelegramRequest request, CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var settingsService = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        var telegramService = scope.ServiceProvider.GetRequiredService<ITelegramService>();
        var aiService = scope.ServiceProvider.GetRequiredService<IAiService>();
        var investecClient = scope.ServiceProvider.GetRequiredService<IInvestecClient>();
        var actuarialService = scope.ServiceProvider.GetRequiredService<IActuarialService>();
        var chartService = scope.ServiceProvider.GetRequiredService<IChartService>();
        var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var botClientFactory = scope.ServiceProvider.GetRequiredService<ITelegramBotClientFactory>();
        var memoryCache = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>();

        int placeholderId = 0;
        CancellationTokenSource? ctsHeartbeat = null;

        try
        {
            _logger.LogInformation("Processing Telegram message for user {UserId}", request.UserId);

            var settings = await settingsService.GetSettingsAsync(request.UserId);
            var botClient = botClientFactory.GetClient(settings.TelegramBotToken);

            // Handle Slash Commands
            if (!string.IsNullOrWhiteSpace(request.MessageText) && request.MessageText.StartsWith("/"))
            {
                var cmd = request.MessageText.Split(' ')[0].ToLower();
                
                // Log the command for debugging
                _logger.LogDebug("Processing command '{Cmd}' for user {UserId}", cmd, request.UserId);
                
                if (cmd == "/clear")
                {
                    await telegramService.SendMessageWithButtonsAsync(request.UserId, 
                        "⚠️ <b>Warning: Clear History</b>\n\nThis will permanently delete your entire conversation history with the AI. This action cannot be undone.\n\nAre you sure?",
                        new List<(string Text, string CallbackData)> { ("Yes, Clear It", "/clear_confirmed"), ("No, Cancel", "/cancel") }, 
                        request.ChatId);
                    return;
                }
                if (cmd == "/clear_confirmed")
                {
                    try
                    {
                        using var dbConfirm = new NpgsqlConnection(config.GetConnectionString("DefaultConnection"));
                        await dbConfirm.ExecuteAsync("DELETE FROM chat_history WHERE user_id = @UserId", new { UserId = request.UserId });
                        await telegramService.SendMessageAsync(request.UserId, "✅ <b>Success:</b> Your conversation history has been cleared.", request.ChatId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to clear chat history for user {UserId}", request.UserId);
                        await telegramService.SendMessageAsync(request.UserId, "❌ <b>Error:</b> Failed to clear history. Please try again.", request.ChatId);
                    }
                    return;
                }
                if (cmd == "/cancel")
                {
                    await telegramService.SendMessageAsync(request.UserId, "Action cancelled.", request.ChatId);
                    return;
                }
                if (cmd == "/model")
                {
                    await telegramService.SendMessageWithButtonsAsync(request.UserId, 
                        "⚙️ <b>AI Model Configuration</b>\n\nWhich provider would you like to configure?",
                        new List<(string Text, string CallbackData)> { ("Primary AI", "/model_provider_primary"), ("Backup AI", "/model_provider_backup") }, 
                        request.ChatId);
                    return;
                }
                if (cmd == "/model_provider_primary" || cmd == "/model_provider_backup")
                {
                    try
                    {
                        bool isPrimary = cmd.EndsWith("primary");
                        var provider = isPrimary ? settings.AiProvider : settings.FallbackAiProvider;
                        await telegramService.SendMessageWithButtonsAsync(request.UserId, 
                            $"⚙️ <b>Select Provider for {(isPrimary ? "Primary" : "Backup")}</b>\n\nCurrent: <i>{provider}</i>",
                            new List<(string Text, string CallbackData)> { 
                                ("Ollama", $"/model_select_{ (isPrimary ? "p" : "b") }_ollama"), 
                                ("Gemini", $"/model_select_{ (isPrimary ? "p" : "b") }_gemini") 
                            }, 
                            request.ChatId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to show provider selection for user {UserId}", request.UserId);
                        await telegramService.SendMessageAsync(request.UserId, "❌ <b>Error:</b> Failed to load provider selection.", request.ChatId);
                    }
                    return;
                }
                if (cmd.StartsWith("/model_select_")) // e.g. /model_select_p_ollama
                {
                    try
                    {
                        var parts = cmd.Split('_');
                        // Validate we have enough parts: ["", "model", "select", "p", "ollama"] = 5 parts minimum
                        if (parts.Length < 5)
                        {
                            _logger.LogWarning("Invalid model_select command format: {Cmd}", cmd);
                            await telegramService.SendMessageAsync(request.UserId, "❌ <b>Error:</b> Invalid command format.", request.ChatId);
                            return;
                        }
                        
                        bool isPrimary = parts[3] == "p";
                        var providerName = parts[4]; // "ollama" or "gemini"
                        
                        // Validate provider name
                        if (providerName != "ollama" && providerName != "gemini")
                        {
                            _logger.LogWarning("Invalid provider in model_select command: {Provider}", providerName);
                            await telegramService.SendMessageAsync(request.UserId, "❌ <b>Error:</b> Invalid provider.", request.ChatId);
                            return;
                        }
                        
                        string currentModel;
                        if (isPrimary)
                            currentModel = settings.AiProvider == "Gemini" ? settings.GeminiModelName : settings.OllamaModelName;
                        else
                            currentModel = settings.FallbackAiProvider == "Gemini" ? settings.FallbackGeminiModelName : settings.FallbackOllamaModelName;
                        
                        // Create temporary settings to fetch models for the SELECTED provider
                        var tempSettings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
                        if (isPrimary) tempSettings.AiProvider = providerName == "ollama" ? "Ollama" : "Gemini";
                        else tempSettings.FallbackAiProvider = providerName == "ollama" ? "Ollama" : "Gemini";

                        var models = await aiService.GetAvailableModelsAsync(request.UserId, !isPrimary, false, tempSettings);
                        var buttons = models.Take(8).Select(m => (m, $"/model_set_{ (isPrimary ? "p" : "b") }_{providerName}_{m}")).ToList();
                        buttons.Add(("Cancel", "/cancel"));

                        await telegramService.SendMessageWithButtonsAsync(request.UserId, 
                            $"⚙️ <b>Select Model ({(isPrimary ? "Primary" : "Backup")})</b>\n\nProvider: {providerName.ToUpper()}\nCurrent: {currentModel}",
                            buttons, 
                            request.ChatId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to show model selection for user {UserId}", request.UserId);
                        await telegramService.SendMessageAsync(request.UserId, "❌ <b>Error:</b> Failed to load model selection.", request.ChatId);
                    }
                    return;
                }
                if (cmd.StartsWith("/model_set_")) // e.g. /model_set_p_ollama_llama3
                {
                    try
                    {
                        var parts = cmd.Split('_');
                        // Validate we have enough parts: ["", "model", "set", "p", "ollama", "modelname"] = 6 parts minimum
                        if (parts.Length < 6)
                        {
                            _logger.LogWarning("Invalid model_set command format: {Cmd}", cmd);
                            await telegramService.SendMessageAsync(request.UserId, "❌ <b>Error:</b> Invalid command format.", request.ChatId);
                            return;
                        }
                        
                        bool isPrimary = parts[3] == "p";
                        var provider = parts[4];
                        
                        // Validate provider
                        if (provider != "ollama" && provider != "gemini")
                        {
                            _logger.LogWarning("Invalid provider in model_set command: {Provider}", provider);
                            await telegramService.SendMessageAsync(request.UserId, "❌ <b>Error:</b> Invalid provider.", request.ChatId);
                            return;
                        }
                        
                        // Model name is everything after the 5th part (index 4), joined back with underscores
                        // This handles model names that contain underscores like "gemini-1.5-pro"
                        var modelName = string.Join("_", parts.Skip(5));
                        
                        if (string.IsNullOrWhiteSpace(modelName))
                        {
                            _logger.LogWarning("Empty model name in model_set command");
                            await telegramService.SendMessageAsync(request.UserId, "❌ <b>Error:</b> Model name is required.", request.ChatId);
                            return;
                        }

                        var current = await settingsService.GetSettingsAsync(request.UserId);
                        if (isPrimary) {
                            current.AiProvider = provider == "ollama" ? "Ollama" : "Gemini";
                            if (provider == "ollama") current.OllamaModelName = modelName;
                            else current.GeminiModelName = modelName;
                        } else {
                            current.FallbackAiProvider = provider == "ollama" ? "Ollama" : "Gemini";
                            if (provider == "ollama") current.FallbackOllamaModelName = modelName;
                            else current.FallbackGeminiModelName = modelName;
                        }

                        await settingsService.UpdateSettingsAsync(request.UserId, current);
                        await telegramService.SendMessageAsync(request.UserId, $"✅ <b>Success:</b> {(isPrimary ? "Primary" : "Backup")} AI updated to <b>{modelName}</b> ({provider.ToUpper()}).", request.ChatId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to set model for user {UserId}", request.UserId);
                        await telegramService.SendMessageAsync(request.UserId, "❌ <b>Error:</b> Failed to update model settings.", request.ChatId);
                    }
                    return;
                }
            }

            // Send typing indicator
            await botClient.SendChatAction(request.ChatId, Telegram.Bot.Types.Enums.ChatAction.Typing, cancellationToken: ct);

            // Send placeholder with progress
            placeholderId = await telegramService.SendMessageWithIdAsync(request.UserId,
                "<b>Analytical Engine Working</b>\n" +
                "<code>▱▱▱▱▱▱▱</code>\n" +
                "<i>Initializing financial analysis...</i>", request.ChatId);

            // Start heartbeat
            ctsHeartbeat = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var heartbeatTask = StartHeartbeatAsync(request.UserId, placeholderId, request.ChatId, telegramService, ctsHeartbeat.Token);

            // Configure Investec
            investecClient.Configure(settings.InvestecClientId, settings.InvestecSecret, settings.InvestecApiKey);

            // Fetch data
            using var db = new NpgsqlConnection(config.GetConnectionString("DefaultConnection"));
            var history = (await db.QueryAsync<Transaction>(
                "SELECT * FROM transactions WHERE user_id = @userId AND transaction_date >= NOW() - INTERVAL '90 days' ORDER BY transaction_date ASC",
                new { userId = request.UserId })).ToList();

            var cacheKey = $"investec_balance_{request.UserId}";
            if (!memoryCache.TryGetValue(cacheKey, out decimal currentBalance))
            {
                var accounts = await investecClient.GetAccountsAsync();
                currentBalance = 0;
                foreach (var acc in accounts) currentBalance += await investecClient.GetAccountBalanceAsync(acc.AccountId);
                
                memoryCache.Set(cacheKey, currentBalance, TimeSpan.FromMinutes(15));
            }

            var summary = await actuarialService.AnalyzeHealthAsync(history, currentBalance, settings);
            var summaryJson = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });

            // Check for chart request
            var (isChart, chartType, chartSql, chartTitle) = await aiService.AnalyzeChartRequestAsync(request.UserId, request.MessageText);

            if (isChart && !string.IsNullOrWhiteSpace(chartSql))
            {
                await HandleChartRequestAsync(request.UserId, chartSql!, chartType ?? "bar", chartTitle!, request.ChatId,
                    db, aiService, chartService, telegramService, placeholderId, ctsHeartbeat);
                return;
            }

            // Check for runway chart
            if (!string.IsNullOrWhiteSpace(request.MessageText) && 
                request.MessageText.ToLower().Contains("runway") &&
                (request.MessageText.ToLower().Contains("chart") || request.MessageText.ToLower().Contains("graph")))
            {
                var chartBytes = chartService.GenerateRunwayChart(history, currentBalance, (double)summary.WeightedDailyBurn);
                ctsHeartbeat.Cancel();
                await telegramService.SendImageAsync(request.UserId, chartBytes, "<b>📉 Financial Runway Projection</b>", request.ChatId);
                await telegramService.EditMessageAsync(request.UserId, placeholderId, "Visual runway projection generated.", request.ChatId);
                return;
            }

            // Check for transaction explanation
            var (explainedTxId, explanationNote) = await aiService.AnalyzeExpenseExplanationAsync(request.UserId, request.MessageText ?? "", history);

            if (explainedTxId != null)
            {
                await HandleTransactionExplanationAsync(request.UserId, explainedTxId.Value, explanationNote!, request.MessageText ?? "",
                    request.ChatId, db, history, telegramService, placeholderId, ctsHeartbeat);
                return;
            }

            // Check for affordability question
            var (isAffordability, affordAmount, affordDesc) = await aiService.AnalyzeAffordabilityAsync(request.UserId, request.MessageText ?? "");

            if (isAffordability && affordAmount > 0)
            {
                await HandleAffordabilityCheckAsync(request.UserId, affordAmount!.Value, affordDesc!, request.ChatId,
                    history, currentBalance, summary, settings, actuarialService, chartService, telegramService, placeholderId, ctsHeartbeat);
                return;
            }

            // Standard financial query
            await HandleStandardQueryAsync(request.UserId, request.MessageText ?? "", request.ChatId, currentBalance, summary,
                summaryJson, db, aiService, telegramService, placeholderId, ctsHeartbeat);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Telegram processing cancelled for user {UserId}", request.UserId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing Telegram message for user {UserId}", request.UserId);

            try
            {
                var telegramSvc = scope.ServiceProvider.GetRequiredService<ITelegramService>();
                var errorMessage = "⚠️ <b>Analytical Error</b>\n\nI encountered an unexpected issue while processing your request. Your data is safe. Please try again in a moment.";

                if (placeholderId > 0)
                    await telegramSvc.EditMessageAsync(request.UserId, placeholderId, errorMessage, request.ChatId);
                else
                    await telegramSvc.SendMessageAsync(request.UserId, errorMessage, request.ChatId);
            }
            catch (Exception ex2)
            {
                _logger.LogError(ex2, "Failed to send error message to user {UserId}", request.UserId);
            }
        }
        finally
        {
            ctsHeartbeat?.Cancel();
            ctsHeartbeat?.Dispose();
        }
    }

    private async Task StartHeartbeatAsync(int userId, int placeholderId, string chatId, ITelegramService telegramService, CancellationToken ct)
    {
        var wittyComments = new[]
        {
            "Stress-testing current liquidity buffers...",
            "Assessing variance in discretionary expenditure...",
            "Optimizing capital allocation projections...",
            "Benchmarking against historical salary cycles...",
            "Recalibrating actuarial risk parameters...",
            "Synthesizing transaction metadata...",
            "Running Monte-Carlo runway simulations...",
            "Auditing ledger for deterministic fingerprints...",
            "Evaluating solvency relative to next payday...",
            "Analyzing burn-rate velocity trends..."
        };

        int stageIndex = 0;
        string[] progressStages = { "▰▱▱▱▱▱▱", "▰▰▱▱▱▱▱", "▰▰▰▱▱▱▱", "▰▰▰▰▱▱▱", "▰▰▰▰▰▱▱", "▰▰▰▰▰▰▱", "▰▰▰▰▰▰▰" };

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2.5), ct);

                var bar = progressStages[Math.Min(stageIndex, progressStages.Length - 1)];
                var nextWitty = stageIndex switch
                {
                    0 => "Synchronizing Investec ledger data...",
                    1 => "Running actuarial burn-rate simulations...",
                    2 => "Generating strategic CFO commentary...",
                    3 => "Finalizing liquidity risk assessment...",
                    _ => wittyComments[Random.Shared.Next(wittyComments.Length)]
                };

                stageIndex++;

                if (placeholderId > 0)
                {
                    await telegramService.EditMessageAsync(userId, placeholderId,
                        $"<b>Analytical Engine Working</b>\n" +
                        $"<code>{bar}</code>\n" +
                        $"<i>{TelegramService.EscapeHtml(nextWitty)}</i>", chatId);
                }
            }
            catch (TaskCanceledException) { break; }
            catch (Exception ex) { _logger.LogWarning("Heartbeat error: {Msg}", ex.Message); }
        }
    }

    private async Task HandleChartRequestAsync(int userId, string chartSql, string chartType, string chartTitle, string chatId,
        NpgsqlConnection db, IAiService aiService, IChartService chartService, ITelegramService telegramService,
        int placeholderId, CancellationTokenSource ctsHeartbeat)
    {
        try
        {
            var chartDataRaw = await db.QueryAsync<dynamic>(chartSql, new { userId });
            var chartData = chartDataRaw.Select(d =>
            {
                var dict = (IDictionary<string, object>)d;
                return (Label: dict["label"]?.ToString() ?? "", Value: Convert.ToDouble(dict["value"] ?? 0.0));
            }).ToList();

            if (chartData.Any())
            {
                var chartBytes = chartService.GenerateGenericChart(chartTitle, chartType, chartData);
                var dataJson = JsonSerializer.Serialize(chartData);
                var commentaryPrompt = $@"You are the user's Personal CFO.
The user requested a chart: '{chartTitle}'.
DATA RETRIEVED: {dataJson}

INSTRUCTIONS:
- Provide a 2-sentence strategic observation about this specific data.
- Mention any concerning trends or positive patterns.
- Maintain a highly professional, boardroom tone.";

                var caption = await aiService.FormatResponseAsync(userId, commentaryPrompt, "", isWhatsApp: false);

                ctsHeartbeat.Cancel();
                await telegramService.SendImageAsync(userId, chartBytes, $"<b>📊 {TelegramService.EscapeHtml(chartTitle)}</b>\n\n{caption}", chatId);
                if (placeholderId > 0) await telegramService.EditMessageAsync(userId, placeholderId, "Analytical visualization complete.", chatId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate chart for user {UserId}", userId);
        }
    }

    private async Task HandleTransactionExplanationAsync(int userId, Guid txId, string note, string messageText, string chatId,
        NpgsqlConnection db, List<Transaction> history, ITelegramService telegramService, int placeholderId, CancellationTokenSource ctsHeartbeat)
    {
        await db.ExecuteAsync("UPDATE transactions SET notes = @Note WHERE id = @Id AND user_id = @UserId",
            new { Note = note, Id = txId, UserId = userId });

        var tx = history.FirstOrDefault(t => t.Id == txId);
        var confirmation = $"✅ <b>Noted.</b> I've updated the ledger:\n<i>{TelegramService.EscapeHtml(tx?.Description ?? "Transaction")}</i>: {TelegramService.EscapeHtml(note)}";

        await db.ExecuteAsync("INSERT INTO chat_history (user_id, message_text, is_user) VALUES (@UserId, @Text, TRUE)",
            new { UserId = userId, Text = messageText });
        await db.ExecuteAsync("INSERT INTO chat_history (user_id, message_text, is_user) VALUES (@UserId, @Text, FALSE)",
            new { UserId = userId, Text = confirmation });

        ctsHeartbeat.Cancel();
        if (placeholderId > 0) await telegramService.EditMessageAsync(userId, placeholderId, confirmation, chatId);
        else await telegramService.SendMessageAsync(userId, confirmation, chatId);
    }

    private async Task HandleAffordabilityCheckAsync(int userId, decimal amount, string description, string chatId,
        List<Transaction> history, decimal currentBalance, FinancialHealthReport summary, AppSettings settings,
        IActuarialService actuarialService, IChartService chartService, ITelegramService telegramService,
        int placeholderId, CancellationTokenSource ctsHeartbeat)
    {
        var simulatedBalance = currentBalance - amount;
        var simSummary = await actuarialService.AnalyzeHealthAsync(history, simulatedBalance, settings);

        var runwayImpact = summary.ExpectedRunwayDays - simSummary.ExpectedRunwayDays;
        var riskLevel = simSummary.ExpectedRunwayDays < 10 ? "High" : runwayImpact > 5 ? "Medium" : "Low";

        var response = $"<b>Affordability Analysis: {TelegramService.EscapeHtml(description)}</b>\n" +
                       $"-----------------------------\n" +
                       $"<b>Price:</b> R{amount:N2}\n" +
                       $"<b>New Balance:</b> R{simulatedBalance:N2}\n" +
                       $"<b>Runway Impact:</b> -{runwayImpact:F1} days\n" +
                       $"<b>New Runway:</b> {simSummary.ExpectedRunwayDays:F1} days\n" +
                       $"<b>Risk Level:</b> {riskLevel}\n\n" +
                       (riskLevel == "High" ? "🛑 <b>ADVISORY:</b> This purchase puts you in a dangerous liquidity position." :
                        "✅ <b>ADVISORY:</b> You have sufficient buffer for this.");

        ctsHeartbeat.Cancel();
        if (placeholderId > 0) await telegramService.EditMessageAsync(userId, placeholderId, response, chatId);
        else await telegramService.SendMessageAsync(userId, response, chatId);

        if (riskLevel == "High")
        {
            var chartBytes = chartService.GenerateRunwayChart(history, simulatedBalance, (double)summary.WeightedDailyBurn);
            await telegramService.SendImageAsync(userId, chartBytes, "<b>📉 Projected Impact Visualization</b>", chatId);
        }
    }

    private async Task HandleStandardQueryAsync(int userId, string messageText, string chatId, decimal currentBalance,
        FinancialHealthReport summary, string summaryJson, NpgsqlConnection db, IAiService aiService,
        ITelegramService telegramService, int placeholderId, CancellationTokenSource ctsHeartbeat)
    {
        var culture = (System.Globalization.CultureInfo)System.Globalization.CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.CurrencySymbol = "R";
        var statsBlock = $"<b>Financial Position Update</b>\n" +
                         $"---------------------------\n" +
                         $"<b>Current Balance:</b> {TelegramService.EscapeHtml(currentBalance.ToString("C", culture))}\n" +
                         $"<b>Projected Runway:</b> {(summary.ExpectedRunwayDays < 0 ? "0" : TelegramService.EscapeHtml(summary.ExpectedRunwayDays.ToString("F0")))} Days\n" +
                         $"<b>Next Salary:</b> In {summary.DaysUntilNextSalary} Days\n" +
                         $"<b>Burn Trend:</b> {TelegramService.EscapeHtml(summary.TrendDirection)}\n" +
                         $"---------------------------\n\n";

        var recentHistory = (await db.QueryAsync<(string Text, bool IsUser)>(
            "SELECT message_text, is_user FROM chat_history WHERE user_id = @userId ORDER BY timestamp DESC LIMIT 10",
            new { userId })).Reverse().ToList();

        var historyContext = string.Join("\n", recentHistory.Select(h => h.IsUser ? $"User: {h.Text}" : $"CFO: {h.Text}"));

        var promptForSummary = $@"You are acting as the user's Personal CFO.

**PREVIOUS CONVERSATION:**
{historyContext}

**CURRENT REQUEST:**
User: {messageText}

**INSTRUCTIONS:**
- Provide a direct, data-driven answer based *only* on the provided financial summary and previous conversation context.
- If the user's question implies financial stress, provide a path to stability.
- If the user's question implies good health, suggest how to optimize or invest.
- Maintain a tone of calm, professional competence.
- Do NOT repeat the header stats (Balance, Runway, etc.) as they are already displayed above your message.

**YOUR GOAL:**
Demonstrate that you understand their financial reality better than they do, and guide them toward control.";

        var aiResponse = await aiService.FormatResponseAsync(userId, promptForSummary, summaryJson, isWhatsApp: false);

        if (string.IsNullOrWhiteSpace(aiResponse) || aiResponse.Contains("I'm sorry") || aiResponse.Contains("difficulties connecting"))
        {
            aiResponse = "<i>I'm currently observing some latency in my analytical engine. Your core metrics are displayed above for your review. The system remains operational and your data is secure.</i>";
        }

        var finalAnswer = statsBlock + aiResponse;

        await db.ExecuteAsync("INSERT INTO chat_history (user_id, message_text, is_user) VALUES (@UserId, @Text, TRUE)",
            new { UserId = userId, Text = messageText });
        await db.ExecuteAsync("INSERT INTO chat_history (user_id, message_text, is_user) VALUES (@UserId, @Text, FALSE)",
            new { UserId = userId, Text = aiResponse });

        ctsHeartbeat.Cancel();

        if (placeholderId > 0)
            await telegramService.EditMessageAsync(userId, placeholderId, finalAnswer, chatId);
        else
            await telegramService.SendMessageAsync(userId, finalAnswer, chatId);
    }

    private record TelegramRequest(int UserId, string ChatId, string MessageText);
}
