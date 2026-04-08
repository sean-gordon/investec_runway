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

    // Hard ceiling for the entire Telegram reply pipeline. Beyond this the user has lost patience
    // and the AI provider is almost certainly stuck retrying — better to bail and tell them.
    private static readonly TimeSpan TelegramProcessingBudget = TimeSpan.FromSeconds(90);

    private async Task ProcessMessageAsync(TelegramRequest request, CancellationToken outerCt)
    {
        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        budgetCts.CancelAfter(TelegramProcessingBudget);
        var ct = budgetCts.Token;

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
            var intentTask = aiService.DetectIntentAsync(request.UserId, request.MessageText, ct);
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
                // Parallel balance fetches — was sequential foreach (N round-trips against the
                // Investec API), now fan out and join. Cuts Telegram first-byte by ~N×latency.
                var balances = await Task.WhenAll(accounts.Select(acc => investecClient.GetAccountBalanceAsync(acc.AccountId)));
                currentBalance = balances.Sum();
                memoryCache.Set(cacheKey, currentBalance, TimeSpan.FromMinutes(15));
            }

            // 5. Kick off actuarial analysis as a deferred task. We won't await it here — branches
            //    that need it will await as late as possible. AFFORD specifically can fan out the
            //    simulated analysis alongside this one, halving total actuarial wall-clock.
            var summaryTask = actuarialService.AnalyzeHealthAsync(history, currentBalance, settings);

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
                        repo, aiService, chartService, telegramService, placeholderId, ctsHeartbeat, ct);
                    return;
                }
            }
            else if (intent == "EXPLAIN")
            {
                var explanationResult = await aiService.AnalyzeExpenseExplanationAsync(request.UserId, request.MessageText, history, ct);
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
                    // Fan out the simulated-balance analysis ALONGSIDE the in-flight summaryTask.
                    // Both run on the same in-memory history list and are CPU-bound, so this nearly
                    // halves the actuarial wall-clock time before the AI verdict prompt fires.
                    var simulatedBalance = currentBalance - amount;
                    var simSummaryTask = actuarialService.AnalyzeHealthAsync(history, simulatedBalance, settings);
                    await Task.WhenAll(summaryTask, simSummaryTask);
                    var summaryForAfford = await summaryTask;
                    var simSummary = await simSummaryTask;

                    await HandleAffordabilityCheckAsync(request.UserId, amount, desc!, request.ChatId,
                        history, currentBalance, simulatedBalance, summaryForAfford, simSummary, settings,
                        aiService, chartService, telegramService, placeholderId, ctsHeartbeat, ct);
                    return;
                }
            }

            // Default path (QUERY, fall-through from CHART without sql, EXPLAIN without match):
            // we now need the summary, so await it here.
            var summary = await summaryTask;
            var summaryJson = JsonSerializer.Serialize(summary);
            await HandleStandardQueryAsync(request.UserId, request.MessageText, request.ChatId, currentBalance, summary,
                summaryJson, repo, aiService, telegramService, placeholderId, ctsHeartbeat, ct);
        }
        catch (OperationCanceledException) when (budgetCts.IsCancellationRequested && !outerCt.IsCancellationRequested)
        {
            // Hit our own 90s budget — almost always means the AI provider is overloaded and the
            // retry loop has been spinning. Tell the user clearly instead of leaving them staring
            // at a frozen progress bar.
            _logger.LogWarning("Telegram processing exceeded {Budget}s budget for user {UserId}",
                TelegramProcessingBudget.TotalSeconds, request.UserId);
            ctsHeartbeat?.Cancel();
            try
            {
                var telegramSvc = scope.ServiceProvider.GetRequiredService<ITelegramService>();
                var timeoutMessage =
                    "⏳ <b>Taking longer than expected</b>\n\n" +
                    "My analytical engine is under heavy load right now (the AI provider is " +
                    "throttling requests). Please try again in a minute or two.";
                if (placeholderId > 0) await telegramSvc.EditMessageAsync(request.UserId, placeholderId, timeoutMessage, request.ChatId);
                else await telegramSvc.SendMessageAsync(request.UserId, timeoutMessage, request.ChatId);
            }
            catch (Exception ex2) { _logger.LogError(ex2, "Failed to send timeout message."); }
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
                // User-facing copy stays calm and actionable; the raw exception is in the logs above.
                var errorMessage =
                    "⚠️ <b>Analytical engine hiccup</b>\n\n" +
                    "I couldn't complete that analysis just now. Please try again in a moment — " +
                    "if it keeps happening, check the worker logs for details.";
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
        // Deterministic rotation — must NOT pick at random, otherwise consecutive editMessageText
        // calls collide and Telegram returns "message is not modified" 400s.
        var wittyComments = new[]
        {
            "Stress-testing liquidity buffers",
            "Assessing variance in expenditure",
            "Optimizing capital projections",
            "Benchmarking salary cycles",
            "Recalibrating risk parameters",
            "Synthesizing transaction metadata",
            "Running runway simulations",
            "Auditing ledger fingerprints"
        };
        string[] progressStages = { "▰▱▱▱▱▱▱", "▰▰▱▱▱▱▱", "▰▰▰▱▱▱▱", "▰▰▰▰▱▱▱", "▰▰▰▰▰▱▱", "▰▰▰▰▰▰▱", "▰▰▰▰▰▰▰" };

        // Heartbeat caps itself well inside the overall processing budget so it never out-lives
        // a stuck AI call. After this point we leave a calm "still working" message in place.
        var heartbeatBudget = TimeSpan.FromSeconds(45);
        var startedAt = DateTime.UtcNow;
        int tick = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
                if (ct.IsCancellationRequested) break;

                var elapsed = DateTime.UtcNow - startedAt;
                if (elapsed > heartbeatBudget)
                {
                    // Final state — written exactly once, then the heartbeat goes quiet.
                    if (placeholderId > 0)
                    {
                        await telegramService.EditMessageAsync(userId, placeholderId,
                            "<b>Analytical Engine Working</b>\n" +
                            "<code>▰▰▰▰▰▰▰</code>\n" +
                            "<i>Still crunching — the AI provider is taking its time…</i>", chatId);
                    }
                    return;
                }

                var bar = progressStages[Math.Min(tick, progressStages.Length - 1)];
                var witty = wittyComments[tick % wittyComments.Length];
                // Rotating dot count guarantees the rendered string changes every tick even after
                // the bar maxes out, so Telegram never rejects an edit as "not modified".
                var dots = new string('.', (tick % 3) + 1);

                tick++;
                if (placeholderId > 0)
                {
                    await telegramService.EditMessageAsync(userId, placeholderId,
                        $"<b>Analytical Engine Working</b>\n" +
                        $"<code>{bar}</code>\n" +
                        $"<i>{TelegramService.EscapeHtml(witty)}{dots}</i>", chatId);
                }
            }
            catch (TaskCanceledException) { break; }
            catch (Exception ex) { _logger.LogWarning("Heartbeat error: {Msg}", ex.Message); }
        }
    }

    private async Task HandleChartRequestAsync(int userId, string chartSql, string chartType, string chartTitle, string chatId,
        ITransactionRepository repo, IAiService aiService, IChartService chartService, ITelegramService telegramService,
        int placeholderId, CancellationTokenSource? ctsHeartbeat, CancellationToken ct)
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
            var caption = await aiService.FormatResponseAsync(userId, commentaryPrompt, "", isWhatsApp: false, ct: ct);

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
        List<Transaction> history, decimal currentBalance, decimal simulatedBalance,
        FinancialHealthReport summary, FinancialHealthReport simSummary, AppSettings settings,
        IAiService aiService, IChartService chartService, ITelegramService telegramService,
        int placeholderId, CancellationTokenSource? ctsHeartbeat, CancellationToken ct)
    {
        var runwayImpact = summary.ExpectedRunwayDays - simSummary.ExpectedRunwayDays;
        var riskLevel = simSummary.ExpectedRunwayDays < 10 ? "High" : runwayImpact > 5 ? "Medium" : "Low";

        // Ask the AI to act as the user's personal banker and give a clear verdict.
        // Best-effort: if the AI is unavailable, fall back to a deterministic rule-based verdict
        // so the user always gets actionable guidance, never just a numbers dump.
        string verdict;
        try
        {
            var verdictPrompt = GordonWorker.Prompts.SystemPrompts.GetAffordabilityVerdictPrompt(
                description, amount, currentBalance, simulatedBalance,
                summary.ExpectedRunwayDays, simSummary.ExpectedRunwayDays, runwayImpact,
                summary.DaysUntilNextSalary, riskLevel);
            verdict = await aiService.FormatResponseAsync(userId, verdictPrompt, "", isWhatsApp: false, ct: ct);
            if (string.IsNullOrWhiteSpace(verdict) || verdict.Contains("I'm sorry") || verdict.Contains("difficulties connecting"))
            {
                verdict = BuildFallbackAffordabilityVerdict(description, amount, simulatedBalance,
                    simSummary.ExpectedRunwayDays, summary.DaysUntilNextSalary, riskLevel);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Affordability AI verdict failed for user {UserId}; using rule-based fallback.", userId);
            verdict = BuildFallbackAffordabilityVerdict(description, amount, simulatedBalance,
                simSummary.ExpectedRunwayDays, summary.DaysUntilNextSalary, riskLevel);
        }

        var riskBadge = riskLevel switch
        {
            "High" => "🛑",
            "Medium" => "⚠️",
            _ => "✅"
        };

        var response = $"<b>{riskBadge} Affordability Check: {TelegramService.EscapeHtml(description)}</b>\n" +
                       $"-----------------------------\n" +
                       $"<b>Price:</b> R{amount:N2}\n" +
                       $"<b>New Balance:</b> R{simulatedBalance:N2}\n" +
                       $"<b>New Runway:</b> {simSummary.ExpectedRunwayDays:F0} days (was {summary.ExpectedRunwayDays:F0})\n" +
                       $"<b>Days to Payday:</b> {summary.DaysUntilNextSalary}\n" +
                       $"-----------------------------\n\n" +
                       TelegramService.EscapeHtml(verdict);

        ctsHeartbeat?.Cancel();
        if (placeholderId > 0) await telegramService.EditMessageAsync(userId, placeholderId, response, chatId);
        else await telegramService.SendMessageAsync(userId, response, chatId);

        if (riskLevel == "High")
        {
            var chartBytes = chartService.GenerateRunwayChart(history, simulatedBalance, (double)summary.WeightedDailyBurn);
            await telegramService.SendImageAsync(userId, chartBytes, "<b>📉 Projected Impact Visualization</b>", chatId);
        }
    }

    // Deterministic backstop: when the AI is unavailable we still owe the user a clear yes/no
    // verdict instead of a wall of numbers. Phrasing mirrors the AI prompt's tone.
    private static string BuildFallbackAffordabilityVerdict(string description, decimal amount,
        decimal simulatedBalance, decimal newRunwayDays, int daysUntilNextSalary, string riskLevel)
    {
        if (simulatedBalance < 0)
        {
            return $"No, I'd strongly advise against buying {description} right now — it would push your balance below zero, " +
                   $"which means dipping into overdraft or missing essentials. The smart move is to wait until after payday in {daysUntilNextSalary} days, " +
                   $"or set aside a portion of each paycheck until you've saved the R{amount:N2}. You've got this — patience now means peace of mind later.";
        }

        if (riskLevel == "High" || newRunwayDays < daysUntilNextSalary)
        {
            return $"I'd hold off on {description} for now. After this purchase your runway drops to roughly {newRunwayDays:F0} days, " +
                   $"but payday is {daysUntilNextSalary} days away — that's cutting it dangerously close. Wait until your next salary lands and revisit it then; " +
                   $"a few weeks of patience will turn this from a risk into a comfortable yes.";
        }

        if (riskLevel == "Medium")
        {
            return $"Yes, you can afford {description} — but with caution. It noticeably trims your safety buffer, so I'd only go ahead if you're confident " +
                   $"no surprise expenses are coming this month. If you can wait until just after payday, you'll feel a lot more relaxed about it. Either way, " +
                   $"you're in control here.";
        }

        return $"Yes, you can comfortably afford {description}. Your buffer stays healthy and your runway easily covers the gap to payday, " +
               $"so this won't put any strain on your finances. Enjoy it — you've earned it.";
    }

    private async Task HandleStandardQueryAsync(int userId, string messageText, string chatId, decimal currentBalance,
        FinancialHealthReport summary, string summaryJson, ITransactionRepository repo, IAiService aiService,
        ITelegramService telegramService, int placeholderId, CancellationTokenSource? ctsHeartbeat, CancellationToken ct)
    {
        var culture = (System.Globalization.CultureInfo)System.Globalization.CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.CurrencySymbol = "R";
        var statsBlock = $"<b>Financial Position Update</b>\n" +
                         $"---------------------------\n" +
                         $"<b>Current Balance:</b> {TelegramService.EscapeHtml(currentBalance.ToString("C", culture))}\n" +
                         $"<b>Projected Runway:</b> {(summary.ExpectedRunwayDays < 0 ? "0" : TelegramService.EscapeHtml(summary.ExpectedRunwayDays.ToString("F0")))} Days\n" +
                         $"<b>Next Salary:</b> In {summary.DaysUntilNextSalary} Days\n" +
                         $"---------------------------\n\n";

        // Chat history is best-effort context — never fail the whole reply because we couldn't load it.
        var historyContext = string.Empty;
        try
        {
            var recentHistory = (await repo.GetRecentChatHistoryAsync(userId, 5)).Reverse().ToList();
            historyContext = string.Join("\n", recentHistory.Select(h => h.IsUser ? $"User: {h.MessageText}" : $"CFO: {h.MessageText}"));
        }
        catch (Exception histEx)
        {
            _logger.LogWarning(histEx, "Could not load recent chat history for user {UserId}; continuing without context.", userId);
        }

        var promptForSummary = GordonWorker.Prompts.SystemPrompts.GetStandardQuerySummaryPrompt(historyContext, messageText);
        var aiResponse = await aiService.FormatResponseAsync(userId, promptForSummary, summaryJson, isWhatsApp: false, ct: ct);

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
