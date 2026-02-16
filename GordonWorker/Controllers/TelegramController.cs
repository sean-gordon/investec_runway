using Dapper;
using GordonWorker.Models;
using GordonWorker.Services;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace GordonWorker.Controllers;

[ApiController]
[Route("telegram")]
public class TelegramController : ControllerBase
{
    private readonly IAiService _aiService;
    private readonly IActuarialService _actuarialService;
    private readonly ITelegramService _telegramService;
    private readonly ISettingsService _settingsService;
    private readonly ISystemStatusService _statusService;
    private readonly IInvestecClient _investecClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TelegramController> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    public TelegramController(
        IAiService aiService,
        IActuarialService actuarialService,
        ITelegramService telegramService,
        ISettingsService settingsService,
        ISystemStatusService statusService,
        IInvestecClient investecClient,
        IConfiguration configuration,
        ILogger<TelegramController> logger,
        IServiceScopeFactory scopeFactory)
    {
        _aiService = aiService;
        _actuarialService = actuarialService;
        _telegramService = telegramService;
        _settingsService = settingsService;
        _statusService = statusService;
        _investecClient = investecClient;
        _configuration = configuration;
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    [HttpPost("webhook")]
    public async Task<IActionResult> Webhook([FromBody] JsonElement rawUpdate)
    {
        _statusService.LastTelegramHit = DateTime.UtcNow;
        _statusService.LastTelegramError = "";

        try
        {
            var update = JsonSerializer.Deserialize<Update>(rawUpdate.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            
            // Prevent Bot Loop
            if (update?.Message?.From?.IsBot == true) return Ok();

            if (update == null || update.Message == null || string.IsNullOrWhiteSpace(update.Message.Text)) return Ok();

            var chatId = update.Message.Chat.Id.ToString();
            var messageText = update.Message.Text;

            using var connection = new NpgsqlConnection(_configuration.GetConnectionString("DefaultConnection"));
            var allUsers = await connection.QueryAsync<int>("SELECT id FROM users");
            int? matchedUserId = null;

            foreach (var uid in allUsers)
            {
                var s = await _settingsService.GetSettingsAsync(uid);
                if (s.TelegramChatId == chatId || (s.TelegramAuthorizedChatIds ?? "").Split(',').Contains(chatId))
                {
                    matchedUserId = uid;
                    break;
                }
            }

            if (matchedUserId == null)
            {
                _logger.LogWarning("Unauthorized Telegram message from Chat ID {ChatId}", chatId);
                return Ok(); 
            }

            var userId = matchedUserId.Value;
            
            // Fire-and-forget processing using ServiceScopeFactory
            _ = Task.Run(async () => 
            {
                int placeholderId = 0;
                try 
                {
                    using var scope = _scopeFactory.CreateScope();
                    var settingsService = scope.ServiceProvider.GetRequiredService<ISettingsService>();
                    var telegramService = scope.ServiceProvider.GetRequiredService<ITelegramService>();
                    var aiService = scope.ServiceProvider.GetRequiredService<IAiService>();
                    var investecClient = scope.ServiceProvider.GetRequiredService<IInvestecClient>();
                    var actuarialService = scope.ServiceProvider.GetRequiredService<IActuarialService>();
                    var chartService = scope.ServiceProvider.GetRequiredService<IChartService>();
                    var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
                    var logger = scope.ServiceProvider.GetRequiredService<ILogger<TelegramController>>();

                    logger.LogInformation("Processing Telegram message for user {UserId}", userId);

                    var settings = await settingsService.GetSettingsAsync(userId);
                    var botClient = new TelegramBotClient(settings.TelegramBotToken);
                    await botClient.SendChatAction(chatId, Telegram.Bot.Types.Enums.ChatAction.Typing);

                    // 1. Send Placeholder
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
                        "Analyzing burn-rate velocity trends...",
                        "Projecting net-worth lifecycle trajectory...",
                        "Cross-referencing fiscal category variances...",
                        "Calibrating treasury liquidity requirements...",
                        "Reconciling off-balance sheet liabilities...",
                        "Quantifying exposure to lifestyle inflation...",
                        "Auditing the coffee-to-savings ratio...",
                        "Analyzing fiscal elasticity across categories...",
                        "Decoupling fixed costs from variable headwinds...",
                        "Evaluating fiscal drag on emergency reserves...",
                        "Forecasting liquidity through the next quarter...",
                        "Mapping capital flows against strategic goals...",
                        "Optimizing tax-advantaged positioning...",
                        "Recalibrating debt-to-income sensitivity...",
                        "Running sensitivity analysis on discretionary spend...",
                        "Stress-testing the weekend entertainment budget...",
                        "Assessing the impact of impulse acquisition...",
                        "Benchmarking savings rate against peer group...",
                        "Calculating the cost of fiscal procrastination...",
                        "Cross-checking vendor billing for anomalies...",
                        "Detecting patterns in subscription leakage...",
                        "Estimating net-worth velocity at current trajectory...",
                        "Filtering signal from noise in transaction data...",
                        "Gauging the resilience of the primary buffer...",
                        "Identifying opportunities for cost-center reduction...",
                        "Investigating variance in grocery-related spend...",
                        "Justifying capital expenditure on luxury assets...",
                        "Kinetic analysis of outgoing cash flow...",
                        "Leveling up the internal audit protocol...",
                        "Measuring fiscal discipline across time-horizons...",
                        "Normalizing data for seasonal spending peaks...",
                        "Overseeing the redistribution of surplus liquidity...",
                        "Performing a deep-dive on historical anomalies...",
                        "Quantizing the impact of interest rate shifts...",
                        "Re-evaluating the ROI of your dining habits...",
                        "Scrutinizing the ledger for ghost subscriptions...",
                        "Triangulating solvency across multiple accounts...",
                        "Uncovering hidden patterns in weekend burn...",
                        "Validating the integrity of the runway model...",
                        "Weather-proofing the budget for unexpected costs...",
                        "X-raying the portfolio for risk concentration...",
                        "Yield-mapping across your current cash positions...",
                        "Zero-basing the budget for the coming cycle...",
                        "Adjusting for inflationary fiscal pressure...",
                        "Balancing the scales of short-term satisfaction...",
                        "Coordinating with the central bank (my brain)...",
                        "Diversifying the mental model of your wealth...",
                        "Extracting insights from recurring overheads...",
                        "Factoring in the 'Treat Yourself' variance...",
                        "Harmonizing the ledger with real-world activity...",
                        "Interpreting the delta in your net liquidity...",
                        "Navigating the minefield of bank service fees...",
                        "Peer-reviewing your recent fiscal decisions...",
                        "Resolving conflicts between heart and wallet..."
                    };

                    placeholderId = await telegramService.SendMessageWithIdAsync(userId, 
                        "<b>Analytical Engine Working</b>\n" +
                        "<code>▱▱▱▱▱▱▱</code>\n" +
                        "<i>Initializing financial analysis...</i>", chatId);

                    // Start progress heartbeat
                    using var ctsHeartbeat = new CancellationTokenSource();
                    var heartbeatTask = Task.Run(async () => {
                        int stageIndex = 0;
                        string[] progressStages = { "▰▱▱▱▱▱▱", "▰▰▱▱▱▱▱", "▰▰▰▱▱▱▱", "▰▰▰▰▱▱▱", "▰▰▰▰▰▱▱", "▰▰▰▰▰▰▱", "▰▰▰▰▰▰▰" };
                        while (!ctsHeartbeat.Token.IsCancellationRequested)
                        {
                            try {
                                await Task.Delay(TimeSpan.FromSeconds(15), ctsHeartbeat.Token);
                                
                                var bar = progressStages[Math.Min(stageIndex, progressStages.Length - 1)];
                                var nextWitty = stageIndex switch {
                                    0 => "Synchronizing Investec ledger data...",
                                    1 => "Running actuarial burn-rate simulations...",
                                    2 => "Generating strategic CFO commentary...",
                                    3 => "Finalizing liquidity risk assessment...",
                                    4 => "Stress-testing capital allocation...",
                                    5 => "Recalibrating solvency parameters...",
                                    _ => wittyComments[new Random().Next(wittyComments.Length)]
                                };

                                stageIndex++;

                                if (placeholderId > 0)
                                {
                                    await telegramService.EditMessageAsync(userId, placeholderId, 
                                        $"<b>Analytical Engine Working</b>\n" +
                                        $"<code>{bar}</code>\n" +
                                        $"<i>{TelegramService.EscapeHtml(nextWitty)}</i>", chatId);
                                }
                            } catch (TaskCanceledException) { break; }
                            catch (Exception ex) { logger.LogWarning("Heartbeat error: {Msg}", ex.Message); }
                        }
                    });

                    investecClient.Configure(settings.InvestecClientId, settings.InvestecSecret, settings.InvestecApiKey);
                    
                    using var db = new NpgsqlConnection(config.GetConnectionString("DefaultConnection"));
                    var history = (await db.QueryAsync<Transaction>(
                        "SELECT * FROM transactions WHERE user_id = @userId AND transaction_date >= NOW() - INTERVAL '90 days' ORDER BY transaction_date ASC", 
                        new { userId })).ToList();

                    var accounts = await investecClient.GetAccountsAsync();
                    decimal currentBalance = 0;
                    foreach (var acc in accounts) currentBalance += await investecClient.GetAccountBalanceAsync(acc.AccountId);

                    var summary = await actuarialService.AnalyzeHealthAsync(history, currentBalance, settings);
                    var summaryJson = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });

                    // --- 0. Check for Chart Request (Explicit or AI-detected) ---
                    var (isChart, chartType, chartSql, chartTitle) = await aiService.AnalyzeChartRequestAsync(userId, messageText);
                    
                    if (isChart && !string.IsNullOrWhiteSpace(chartSql))
                    {
                        try 
                        {
                            var chartDataRaw = await db.QueryAsync<dynamic>(chartSql, new { userId });
                            var chartData = chartDataRaw.Select(d => {
                                var dict = (IDictionary<string, object>)d;
                                return (Label: dict["Label"]?.ToString() ?? "", Value: Convert.ToDouble(dict["Value"] ?? 0.0));
                            }).ToList();

                            if (chartData.Any())
                            {
                                var chartBytes = chartService.GenerateGenericChart(chartTitle!, chartType ?? "bar", chartData);
                                
                                // Generate AI Commentary for this specific data
                                var dataJson = JsonSerializer.Serialize(chartData);
                                var commentaryPrompt = $@"You are the user's Personal CFO. 
The user requested a chart: '{chartTitle}'.
DATA RETRIEVED: {dataJson}

INSTRUCTIONS:
- Provide a 2-sentence strategic observation about this specific data.
- Mention any concerning trends or positive patterns.
- Maintain a highly professional, boardroom tone.";

                                var caption = await aiService.FormatResponseAsync(userId, commentaryPrompt, "", isWhatsApp: false);
                                
                                ctsHeartbeat.Cancel(); // Stop the heartbeat
                                await telegramService.SendImageAsync(userId, chartBytes, $"<b>📊 {TelegramService.EscapeHtml(chartTitle)}</b>\n\n{caption}", chatId);
                                if (placeholderId > 0) await telegramService.EditMessageAsync(userId, placeholderId, "Analytical visualization complete.", chatId);
                                return;
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "Failed to generate dynamic chart for user {UserId}", userId);
                        }
                    }

                    // --- 0.1 Check for Legacy Runway Chart Request ---
                    if (messageText.ToLower().Contains("runway") && (messageText.ToLower().Contains("chart") || messageText.ToLower().Contains("graph")))
                    {
                        var chartBytes = chartService.GenerateRunwayChart(history, currentBalance, (double)summary.WeightedDailyBurn);
                        ctsHeartbeat.Cancel(); // Stop the heartbeat
                        await telegramService.SendImageAsync(userId, chartBytes, "<b>📉 Financial Runway Projection</b>", chatId);
                        await telegramService.EditMessageAsync(userId, placeholderId, "Visual runway projection generated.", chatId);
                        return;
                    }

                    // --- 1. Check for Transaction Explanation ---
                    var (explainedTxId, explanationNote) = await aiService.AnalyzeExpenseExplanationAsync(userId, messageText, history);
                    
                    if (explainedTxId != null)
                    {
                        // Update DB
                        await db.ExecuteAsync("UPDATE transactions SET notes = @Note WHERE id = @Id AND user_id = @UserId", 
                            new { Note = explanationNote, Id = explainedTxId, UserId = userId });
                        
                        var tx = history.FirstOrDefault(t => t.Id == explainedTxId);
                        var confirmation = $"✅ <b>Noted.</b> I've updated the ledger:\n<i>{TelegramService.EscapeHtml(tx?.Description ?? "Transaction")}</i>: {TelegramService.EscapeHtml(explanationNote)}";
                        
                        // Save history (User Request)
                        await db.ExecuteAsync("INSERT INTO chat_history (user_id, message_text, is_user) VALUES (@UserId, @Text, TRUE)", 
                            new { UserId = userId, Text = messageText });
                        // Save history (AI Response)
                        await db.ExecuteAsync("INSERT INTO chat_history (user_id, message_text, is_user) VALUES (@UserId, @Text, FALSE)", 
                            new { UserId = userId, Text = confirmation });

                        ctsHeartbeat.Cancel(); // Stop the heartbeat
                        if (placeholderId > 0) await telegramService.EditMessageAsync(userId, placeholderId, confirmation, chatId);
                        else await telegramService.SendMessageAsync(userId, confirmation, chatId);
                        
                        return; // Exit early, no need for full financial report
                    }
                    // --------------------------------------------

                    // --- 2. Check for Affordability Question ---
                    var (isAffordability, affordAmount, affordDesc) = await aiService.AnalyzeAffordabilityAsync(userId, messageText);
                    
                    if (isAffordability)
                    {
                        var amount = affordAmount ?? 0; // If null, maybe AI couldn't extract, or user didn't say. We can prompt back or just run generic.
                        
                        if (amount > 0)
                        {
                            // Simulate the purchase
                            var simulatedBalance = currentBalance - amount;
                            var simSummary = await actuarialService.AnalyzeHealthAsync(history, simulatedBalance, settings);
                            
                            var runwayImpact = summary.ExpectedRunwayDays - simSummary.ExpectedRunwayDays;
                            var riskLevel = "Low";
                            if (runwayImpact > 5) riskLevel = "Medium";
                            if (simSummary.ExpectedRunwayDays < 10) riskLevel = "High";

                            var response = $"<b>Affordability Analysis: {TelegramService.EscapeHtml(affordDesc)}</b>\n" +
                                           $"-----------------------------\n" +
                                           $"<b>Price:</b> R{amount:N2}\n" +
                                           $"<b>New Balance:</b> R{simulatedBalance:N2}\n" +
                                           $"<b>Runway Impact:</b> -{runwayImpact:F1} days\n" +
                                           $"<b>New Runway:</b> {simSummary.ExpectedRunwayDays:F1} days\n" +
                                           $"<b>Risk Level:</b> {riskLevel}\n\n" +
                                           (riskLevel == "High" ? "🛑 <b>ADVISORY:</b> This purchase puts you in a dangerous liquidity position." : "✅ <b>ADVISORY:</b> You have sufficient buffer for this.");

                            ctsHeartbeat.Cancel(); // Stop the heartbeat
                            if (placeholderId > 0) await telegramService.EditMessageAsync(userId, placeholderId, response, chatId);
                            else await telegramService.SendMessageAsync(userId, response, chatId);

                            // Send chart if High Risk
                            if (riskLevel == "High")
                            {
                                var chartBytes = chartService.GenerateRunwayChart(history, simulatedBalance, (double)summary.WeightedDailyBurn);
                                await telegramService.SendImageAsync(userId, chartBytes, "<b>📉 Projected Impact Visualization</b>", chatId);
                            }

                            return;
                        }
                    }
                    // --------------------------------------------

                    // Construct Hardcoded Stats Block (Formal)
                    var culture = (System.Globalization.CultureInfo)System.Globalization.CultureInfo.InvariantCulture.Clone();
                    culture.NumberFormat.CurrencySymbol = "R";
                    var statsBlock = $"<b>Financial Position Update</b>\n" +
                                     $"---------------------------\n" +
                                     $"<b>Current Balance:</b> {TelegramService.EscapeHtml(currentBalance.ToString("C", culture))}\n" +
                                     $"<b>Projected Runway:</b> {(summary.ExpectedRunwayDays < 0 ? "0" : TelegramService.EscapeHtml(summary.ExpectedRunwayDays.ToString("F0")))} Days\n" +
                                     $"<b>Next Salary:</b> In {summary.DaysUntilNextSalary} Days\n" +
                                     $"<b>Burn Trend:</b> {TelegramService.EscapeHtml(summary.TrendDirection)}\n" +
                                     $"---------------------------\n\n";

                    // Retrieve recent chat history
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
Demonstrate that you understand their financial reality better than they do, and guide them toward control.
If the provided data context appears incomplete (e.g. R0.00 expected income or missing notes), acknowledge this limitation professionally and suggest the user sync their accounts or provide manual clarification.";

                    var aiResponse = await aiService.FormatResponseAsync(userId, promptForSummary, summaryJson, isWhatsApp: false);
                    
                    // Fallback for AI failures
                    if (string.IsNullOrWhiteSpace(aiResponse) || aiResponse.Contains("I'm sorry") || aiResponse.Contains("Error:"))
                    {
                        aiResponse = "<i>I'm currently observing some latency in my analytical engine. Your core metrics are displayed above for your review.</i>";
                    }

                    string finalAnswer;

                    if (aiResponse.Trim().Equals("NEED_SQL", StringComparison.OrdinalIgnoreCase))
                    {
                        var sql = await aiService.GenerateSqlAsync(userId, messageText);
                        if (!string.IsNullOrWhiteSpace(sql))
                        {
                            try
                            {
                                var result = await db.QueryAsync(sql);
                                finalAnswer = statsBlock + await aiService.FormatResponseAsync(userId, messageText, JsonSerializer.Serialize(result), isWhatsApp: false);
                            }
                            catch { finalAnswer = statsBlock + "I encountered an error looking that up."; }
                        }
                        else 
                        {
                            finalAnswer = statsBlock + "I was unable to generate a database query for that request.";
                        }
                    }
                    else 
                    {
                        // Combine Hardcoded Stats + AI Commentary
                        finalAnswer = statsBlock + aiResponse;
                    }

                    // Save history (User Request)
                    await db.ExecuteAsync("INSERT INTO chat_history (user_id, message_text, is_user) VALUES (@UserId, @Text, TRUE)", 
                        new { UserId = userId, Text = messageText });

                    // Save history (AI Response - strip stats block for cleaner history if desired, but keeping mostly clean text is better)
                    // We save the AI part only to avoid storing duplicate stats blocks in history context
                    await db.ExecuteAsync("INSERT INTO chat_history (user_id, message_text, is_user) VALUES (@UserId, @Text, FALSE)", 
                        new { UserId = userId, Text = aiResponse });

                    ctsHeartbeat.Cancel(); // Stop the heartbeat

                    // 2. Edit Message with Final Answer
                    if (placeholderId > 0)
                    {
                        await telegramService.EditMessageAsync(userId, placeholderId, finalAnswer, chatId);
                    }
                    else
                    {
                        await telegramService.SendMessageAsync(userId, finalAnswer, chatId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Background processing error for user {UserId}", userId);
                    try 
                    {
                        using var scope = _scopeFactory.CreateScope();
                        var telegramService = scope.ServiceProvider.GetRequiredService<ITelegramService>();
                        var errorMessage = "⚠️ <b>Analytical Error</b>\nI encountered an unexpected issue while processing your request. Please ensure your accounts are synced and try again.";
                        if (placeholderId > 0) await telegramService.EditMessageAsync(userId, placeholderId, errorMessage, chatId);
                        else await telegramService.SendMessageAsync(userId, errorMessage, chatId);
                    }
                    catch (Exception ex2)
                    {
                        _logger.LogError(ex2, "Failed to send error message to Telegram for user {UserId}", userId);
                    }
                }
            });

            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing Telegram webhook.");
            _statusService.LastTelegramError = ex.Message;
        }

        return Ok();
    }

    [Microsoft.AspNetCore.Authorization.Authorize]
    [HttpPost("setup-webhook")]
    public async Task<IActionResult> SetupWebhook([FromBody] JsonElement body)
    {
        try
        {
            if (!body.TryGetProperty("Url", out var urlElement)) return BadRequest("Missing Url");
            var url = urlElement.GetString();
            if (string.IsNullOrWhiteSpace(url)) return BadRequest("Url empty");
            
            if (User.Identity?.IsAuthenticated != true) return Unauthorized();
            var userId = int.Parse(User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier)!);

            await _telegramService.InstallWebhookAsync(userId, url);
            return Ok(new { Message = "Webhook registered successfully." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to setup Telegram webhook.");
            return StatusCode(500, new { Error = ex.Message });
        }
    }
}
