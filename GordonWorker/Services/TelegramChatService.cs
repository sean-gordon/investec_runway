using GordonWorker.Models;
using GordonWorker.Repositories;
using System.Threading.Channels;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;

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
        var botClientFactory = scope.ServiceProvider.GetRequiredService<ITelegramBotClientFactory>();
        var memoryCache = scope.ServiceProvider.GetRequiredService<IMemoryCache>();
        var repo = scope.ServiceProvider.GetRequiredService<ITransactionRepository>();
        var commandRouter = scope.ServiceProvider.GetRequiredService<ITelegramCommandRouter>();

        int placeholderId = 0;
        CancellationTokenSource? ctsHeartbeat = null;

        try
        {
            _logger.LogInformation("[Telegram] Fast-track processing for User {UserId}", request.UserId);

            var settings = await settingsService.GetSettingsAsync(request.UserId);
            
            // 1. Handle Commands immediately (zero LLM overhead)
            var commandResult = await commandRouter.RouteCommandAsync(request.UserId, request.MessageText, settings, ct);
            if (commandResult != null) return;

            // 2. Parallelize Data Fetching and Intent Detection
            var intentTask = aiService.DetectIntentAsync(request.UserId, request.MessageText);
            var historyTask = repo.GetHistoryForAnalysisAsync(request.UserId, 90);
            
            var botClient = botClientFactory.GetClient(settings.TelegramBotToken);
            await botClient.SendChatAction(request.ChatId, Telegram.Bot.Types.Enums.ChatAction.Typing, cancellationToken: ct);

            // Wait brief moment for instant results before showing progress UI
            var completedTask = await Task.WhenAny(intentTask, Task.Delay(1500, ct));
            
            if (completedTask != intentTask)
            {
                placeholderId = await telegramService.SendMessageWithIdAsync(request.UserId,
                    "<b>Analytical Engine Working</b>\n" +
                    "<code>▱▱▱▱▱▱▱</code>\n" +
                    "<i>Initializing financial analysis...</i>", request.ChatId);

                ctsHeartbeat = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _ = StartHeartbeatAsync(request.UserId, placeholderId, request.ChatId, telegramService, ctsHeartbeat.Token);
            }

            // 3. Resolve Parallel Tasks
            var intentEl = await intentTask;
            var history = (await historyTask).ToList();

            // 4. Get Balances (Cached)
            var cacheKey = $"investec_balance_{request.UserId}";
            if (!memoryCache.TryGetValue(cacheKey, out decimal currentBalance))
            {
                investecClient.Configure(settings.InvestecClientId, settings.InvestecSecret, settings.InvestecApiKey);
                var accounts = await investecClient.GetAccountsAsync();
                currentBalance = 0;
                foreach (var acc in accounts) currentBalance += await investecClient.GetAccountBalanceAsync(acc.AccountId);
                memoryCache.Set(cacheKey, currentBalance, TimeSpan.FromMinutes(15));
            }

            // 5. Actuarial Analysis
            var summary = await actuarialService.AnalyzeHealthAsync(history, currentBalance, settings);
            var summaryJson = JsonSerializer.Serialize(summary);

            // 6. Execute Intent
            var intent = intentEl?.TryGetProperty("intent", out var p) == true ? p.GetString() : "QUERY";

            if (intent == "CHART" && intentEl.Value.TryGetProperty("chart", out var chartObj))
            {
                var chartType = chartObj.TryGetProperty("type", out var ctEl) ? ctEl.GetString() : "bar";
                var chartSql = chartObj.TryGetProperty("sql", out var sqlEl) ? sqlEl.GetString() : null;
                var chartTitle = chartObj.TryGetProperty("title", out var titleEl) ? titleEl.GetString() : "Spending Chart";

                if (!string.IsNullOrWhiteSpace(chartSql))
                {
                    await HandleChartRequestAsync(request.UserId, chartSql, chartType!, chartTitle!, request.ChatId,
                        repo, aiService, chartService, telegramService, placeholderId, ctsHeartbeat);
                    return;
                }
            }
            else if (intent == "EXPLAIN")
            {
                var explanationResult = await aiService.AnalyzeExpenseExplanationAsync(request.UserId, request.MessageText, history);
                if (explanationResult.TransactionId != null)
                {
                    await HandleTransactionExplanationAsync(request.UserId, explanationResult.TransactionId.Value, explanationResult.Note!, request.MessageText,
                        request.ChatId, repo, history, telegramService, placeholderId, ctsHeartbeat);
                    return;
                }
            }
            else if (intent == "AFFORD" && intentEl.Value.TryGetProperty("afford", out var affordObj))
            {
                var amount = affordObj.TryGetProperty("amount", out var amtEl) && amtEl.ValueKind == JsonValueKind.Number ? amtEl.GetDecimal() : 0;
                var desc = affordObj.TryGetProperty("desc", out var descEl) ? descEl.GetString() : "this purchase";

                if (amount > 0)
                {
                    await HandleAffordabilityCheckAsync(request.UserId, amount, desc!, request.ChatId,
                        history, currentBalance, summary, settings, actuarialService, chartService, telegramService, placeholderId, ctsHeartbeat);
                    return;
                }
            }

            await HandleStandardQueryAsync(request.UserId, request.MessageText, request.ChatId, currentBalance, summary,
                summaryJson, repo, aiService, telegramService, placeholderId, ctsHeartbeat);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Telegram processing cancelled for user {UserId}", request.UserId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing Telegram message for user {UserId}", request.UserId);
            ctsHeartbeat?.Cancel();
            try
            {
                var telegramSvc = scope.ServiceProvider.GetRequiredService<ITelegramService>();
                var errorMessage = $"⚠️ <b>Analytical Error</b>\n\n{TelegramService.EscapeHtml(ex.Message)}";
                if (placeholderId > 0) await telegramSvc.EditMessageAsync(request.UserId, placeholderId, errorMessage, request.ChatId);
                else await telegramSvc.SendMessageAsync(request.UserId, errorMessage, request.ChatId);
            }
            catch (Exception ex2) { _logger.LogError(ex2, "Failed to send error message."); }
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
            "Stress-testing liquidity buffers...",
            "Assessing variance in expenditure...",
            "Optimizing capital projections...",
            "Benchmarking salary cycles...",
            "Recalibrating risk parameters...",
            "Synthesizing transaction metadata...",
            "Running runway simulations...",
            "Auditing ledger fingerprints..."
        };

        int stageIndex = 0;
        string[] progressStages = { "▰▱▱▱▱▱▱", "▰▰▱▱▱▱▱", "▰▰▰▱▱▱▱", "▰▰▰▰▱▱▱", "▰▰▰▰▰▱▱", "▰▰▰▰▰▰▱", "▰▰▰▰▰▰▰" };

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
                if (ct.IsCancellationRequested) break;

                var bar = progressStages[Math.Min(stageIndex, progressStages.Length - 1)];
                var nextWitty = wittyComments[Random.Shared.Next(wittyComments.Length)];

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
        ITransactionRepository repo, IAiService aiService, IChartService chartService, ITelegramService telegramService,
        int placeholderId, CancellationTokenSource? ctsHeartbeat)
    {
        var rawChartData = (await repo.GetChartDataAsync(userId, chartSql)).ToList();
        if (rawChartData.Any())
        {
            var chartData = rawChartData.Select(r => {
                var dict = (IDictionary<string, object>)r;
                var label = dict.Values.ElementAtOrDefault(0)?.ToString() ?? "";
                var rawVal = dict.Values.ElementAtOrDefault(1);
                var value = rawVal != null ? Convert.ToDouble(rawVal) : 0.0;
                return (Label: label, Value: value);
            }).ToList();

            var chartBytes = chartService.GenerateGenericChart(chartTitle, chartType, chartData);
            var commentaryPrompt = GordonWorker.Prompts.SystemPrompts.GetChartCommentaryPrompt(chartTitle, JsonSerializer.Serialize(rawChartData));
            var caption = await aiService.FormatResponseAsync(userId, commentaryPrompt, "", isWhatsApp: false);

            ctsHeartbeat?.Cancel();
            await telegramService.SendImageAsync(userId, chartBytes, $"<b>📊 {TelegramService.EscapeHtml(chartTitle)}</b>\n\n{caption}", chatId);
            if (placeholderId > 0) await telegramService.EditMessageAsync(userId, placeholderId, "Analytical visualization complete.", chatId);
        }
        else
        {
            ctsHeartbeat?.Cancel();
            if (placeholderId > 0) await telegramService.EditMessageAsync(userId, placeholderId, "I couldn't find any data to chart for that request.", chatId);
        }
    }

    private async Task HandleTransactionExplanationAsync(int userId, Guid txId, string note, string messageText, string chatId,
        ITransactionRepository repo, List<Transaction> history, ITelegramService telegramService, int placeholderId, CancellationTokenSource? ctsHeartbeat)
    {
        await repo.UpdateTransactionNoteAsync(txId, note);
        var tx = history.FirstOrDefault(t => t.Id == txId);
        var confirmation = $"✅ <b>Noted.</b> I've updated the ledger:\n<i>{TelegramService.EscapeHtml(tx?.Description ?? "Transaction")}</i>: {TelegramService.EscapeHtml(note)}";
        
        await repo.InsertChatHistoryAsync(userId, messageText, true);
        await repo.InsertChatHistoryAsync(userId, confirmation, false);

        ctsHeartbeat?.Cancel();
        if (placeholderId > 0) await telegramService.EditMessageAsync(userId, placeholderId, confirmation, chatId);
        else await telegramService.SendMessageAsync(userId, confirmation, chatId);
    }

    private async Task HandleAffordabilityCheckAsync(int userId, decimal amount, string description, string chatId,
        List<Transaction> history, decimal currentBalance, FinancialHealthReport summary, AppSettings settings,
        IActuarialService actuarialService, IChartService chartService, ITelegramService telegramService,
        int placeholderId, CancellationTokenSource? ctsHeartbeat)
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
                       (riskLevel == "High" ? "🛑 <b>ADVISORY:</b> Dangerous liquidity position." :
                        "✅ <b>ADVISORY:</b> Sufficient buffer for this.");

        ctsHeartbeat?.Cancel();
        if (placeholderId > 0) await telegramService.EditMessageAsync(userId, placeholderId, response, chatId);
        else await telegramService.SendMessageAsync(userId, response, chatId);

        if (riskLevel == "High")
        {
            var chartBytes = chartService.GenerateRunwayChart(history, simulatedBalance, (double)summary.WeightedDailyBurn);
            await telegramService.SendImageAsync(userId, chartBytes, "<b>📉 Projected Impact Visualization</b>", chatId);
        }
    }

    private async Task HandleStandardQueryAsync(int userId, string messageText, string chatId, decimal currentBalance,
        FinancialHealthReport summary, string summaryJson, ITransactionRepository repo, IAiService aiService,
        ITelegramService telegramService, int placeholderId, CancellationTokenSource? ctsHeartbeat)
    {
        var culture = (System.Globalization.CultureInfo)System.Globalization.CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.CurrencySymbol = "R";
        var statsBlock = $"<b>Financial Position Update</b>\n" +
                         $"---------------------------\n" +
                         $"<b>Current Balance:</b> {TelegramService.EscapeHtml(currentBalance.ToString("C", culture))}\n" +
                         $"<b>Projected Runway:</b> {(summary.ExpectedRunwayDays < 0 ? "0" : TelegramService.EscapeHtml(summary.ExpectedRunwayDays.ToString("F0")))} Days\n" +
                         $"<b>Next Salary:</b> In {summary.DaysUntilNextSalary} Days\n" +
                         $"---------------------------\n\n";

        var recentHistory = (await repo.GetRecentChatHistoryAsync(userId, 5)).Reverse().ToList();
        var historyContext = string.Join("\n", recentHistory.Select(h => h.IsUser ? $"User: {h.MessageText}" : $"CFO: {h.MessageText}"));

        var promptForSummary = GordonWorker.Prompts.SystemPrompts.GetStandardQuerySummaryPrompt(historyContext, messageText);
        var aiResponse = await aiService.FormatResponseAsync(userId, promptForSummary, summaryJson, isWhatsApp: false);

        if (string.IsNullOrWhiteSpace(aiResponse) || aiResponse.Contains("I'm sorry") || aiResponse.Contains("difficulties connecting"))
        {
            aiResponse = "<i>analytical engine latency observed. Stats displayed above.</i>";
        }

        var finalAnswer = statsBlock + aiResponse;
        if (finalAnswer.Length > 4000) finalAnswer = finalAnswer.Substring(0, 3990) + "...";

        await repo.InsertChatHistoryAsync(userId, messageText, true);
        await repo.InsertChatHistoryAsync(userId, aiResponse, false);

        ctsHeartbeat?.Cancel();
        if (placeholderId > 0) await telegramService.EditMessageAsync(userId, placeholderId, finalAnswer, chatId);
        else await telegramService.SendMessageAsync(userId, finalAnswer, chatId);
    }

    private record TelegramRequest(int UserId, string ChatId, string MessageText);
}
