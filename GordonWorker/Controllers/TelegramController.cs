using Dapper;
using GordonWorker.Models;
using GordonWorker.Services;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using System.Security.Claims;
using System.Text.Json;
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

                    var settings = await settingsService.GetSettingsAsync(userId);
                    var botClient = new TelegramBotClient(settings.TelegramBotToken);
                    await botClient.SendChatAction(chatId, Telegram.Bot.Types.Enums.ChatAction.Typing);

                    // 1. Send Placeholder
                    var wittyComments = new[]
                    {
                        "Reviewing your latest transaction history...",
                        "Consulting the actuarial models...",
                        "Analyzing your current burn rate...",
                        "Projecting your financial runway...",
                        "Consolidating your financial position..."
                    };
                    var witty = wittyComments[new Random().Next(wittyComments.Length)];
                    var placeholderId = await telegramService.SendMessageWithIdAsync(userId, $"_Processing Request..._ {witty}", chatId);

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
                                
                                await telegramService.SendImageAsync(userId, chartBytes, $"📊 *{chartTitle}*\n\n{caption}", chatId);
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
                        await telegramService.SendImageAsync(userId, chartBytes, "📉 *Financial Runway Projection*", chatId);
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
                        var confirmation = $"✅ *Noted.* I've updated the ledger:\n_{tx?.Description ?? "Transaction"}_: {explanationNote}";
                        
                        // Save history (User Request)
                        await db.ExecuteAsync("INSERT INTO chat_history (user_id, message_text, is_user) VALUES (@UserId, @Text, TRUE)", 
                            new { UserId = userId, Text = messageText });
                        // Save history (AI Response)
                        await db.ExecuteAsync("INSERT INTO chat_history (user_id, message_text, is_user) VALUES (@UserId, @Text, FALSE)", 
                            new { UserId = userId, Text = confirmation });

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

                            var response = $"*Affordability Analysis: {affordDesc}*\n" +
                                           $"-----------------------------\n" +
                                           $"*Price:* R{amount:N2}\n" +
                                           $"*New Balance:* R{simulatedBalance:N2}\n" +
                                           $"*Runway Impact:* -{runwayImpact:F1} days\n" +
                                           $"*New Runway:* {simSummary.ExpectedRunwayDays:F1} days\n" +
                                           $"*Risk Level:* {riskLevel}\n\n" +
                                           (riskLevel == "High" ? "🛑 *ADVISORY:* This purchase puts you in a dangerous liquidity position." : "✅ *ADVISORY:* You have sufficient buffer for this.");

                            if (placeholderId > 0) await telegramService.EditMessageAsync(userId, placeholderId, response, chatId);
                            else await telegramService.SendMessageAsync(userId, response, chatId);

                            // Send chart if High Risk
                            if (riskLevel == "High")
                            {
                                var chartBytes = chartService.GenerateRunwayChart(history, simulatedBalance, (double)summary.WeightedDailyBurn);
                                await telegramService.SendImageAsync(userId, chartBytes, "📉 *Projected Impact Visualization*", chatId);
                            }

                            return;
                        }
                    }
                    // --------------------------------------------

                    // Construct Hardcoded Stats Block (Formal)
                    var culture = (System.Globalization.CultureInfo)System.Globalization.CultureInfo.InvariantCulture.Clone();
                    culture.NumberFormat.CurrencySymbol = "R";
                    var statsBlock = $"*Financial Position Update*\n" +
                                     $"---------------------------\n" +
                                     $"*Current Balance:* {TelegramService.EscapeMarkdownV2(currentBalance.ToString("C", culture))}\n" +
                                     $"*Projected Runway:* {TelegramService.EscapeMarkdownV2(summary.ExpectedRunwayDays.ToString("F0"))} Days\n" +
                                     $"*Next Salary:* In {summary.DaysUntilNextSalary} Days\n" +
                                     $"*Trend:* {TelegramService.EscapeMarkdownV2(summary.TrendDirection)}\n" +
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
Demonstrate that you understand their financial reality better than they do, and guide them toward control.";

                    var aiResponse = await aiService.FormatResponseAsync(userId, promptForSummary, summaryJson, isWhatsApp: false);
                    string finalAnswer;

                    if (aiResponse.Trim().Equals("NEED_SQL", StringComparison.OrdinalIgnoreCase))
                    {
                        var sql = await aiService.GenerateSqlAsync(userId, messageText);
                        try
                        {
                            var result = await db.QueryAsync(sql);
                            finalAnswer = await aiService.FormatResponseAsync(userId, messageText, JsonSerializer.Serialize(result), isWhatsApp: false);
                        }
                        catch { finalAnswer = "I encountered an error looking that up."; }
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
                    Console.WriteLine($"Background processing error: {ex.Message}");
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
