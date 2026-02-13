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

    public TelegramController(
        IAiService aiService,
        IActuarialService actuarialService,
        ITelegramService telegramService,
        ISettingsService settingsService,
        ISystemStatusService statusService,
        IInvestecClient investecClient,
        IConfiguration configuration,
        ILogger<TelegramController> logger)
    {
        _aiService = aiService;
        _actuarialService = actuarialService;
        _telegramService = telegramService;
        _settingsService = settingsService;
        _statusService = statusService;
        _investecClient = investecClient;
        _configuration = configuration;
        _logger = logger;
    }

    [HttpPost("webhook")]
    public async Task<IActionResult> Webhook([FromBody] JsonElement rawUpdate)
    {
        _statusService.LastTelegramHit = DateTime.UtcNow;
        _statusService.LastTelegramError = "";

        try
        {
            var update = JsonSerializer.Deserialize<Update>(rawUpdate.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
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
            
            // Fire-and-forget processing to prevent Telegram timeout/retries
            _ = Task.Run(async () => 
            {
                try 
                {
                    using var scope = HttpContext.RequestServices.CreateScope();
                    var settingsService = scope.ServiceProvider.GetRequiredService<ISettingsService>();
                    var telegramService = scope.ServiceProvider.GetRequiredService<ITelegramService>();
                    var aiService = scope.ServiceProvider.GetRequiredService<IAiService>();
                    var investecClient = scope.ServiceProvider.GetRequiredService<IInvestecClient>();
                    var actuarialService = scope.ServiceProvider.GetRequiredService<IActuarialService>();
                    var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();

                    var settings = await settingsService.GetSettingsAsync(userId);
                    var botClient = new TelegramBotClient(settings.TelegramBotToken);
                    await botClient.SendChatAction(chatId, Telegram.Bot.Types.Enums.ChatAction.Typing);

                    // Re-implement GetFinancialSummaryAsync logic here or make it static/service-based
                    // For simplicity, I'll inline the summary fetch since we are in a static context now
                    
                    investecClient.Configure(settings.InvestecClientId, settings.InvestecSecret, settings.InvestecApiKey);
                    
                    using var connection = new NpgsqlConnection(config.GetConnectionString("DefaultConnection"));
                    var history = (await connection.QueryAsync<Transaction>(
                        "SELECT * FROM transactions WHERE user_id = @userId AND transaction_date >= NOW() - INTERVAL '90 days' ORDER BY transaction_date ASC", 
                        new { userId })).ToList();

                    var accounts = await investecClient.GetAccountsAsync();
                    decimal currentBalance = 0;
                    foreach (var acc in accounts) currentBalance += await investecClient.GetAccountBalanceAsync(acc.AccountId);

                    var summary = await actuarialService.AnalyzeHealthAsync(history, currentBalance, settings);
                    var summaryJson = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });

                    var promptForSummary = $@"Use the provided financial summary to answer the user's question.
If the summary does NOT contain the specific information needed, respond with EXACTLY 'NEED_SQL'.
USER QUESTION: {messageText}";

                    var aiResponse = await aiService.FormatResponseAsync(userId, promptForSummary, summaryJson, isWhatsApp: false);
                    string finalAnswer;

                    if (aiResponse.Trim().Equals("NEED_SQL", StringComparison.OrdinalIgnoreCase))
                    {
                        var sql = await aiService.GenerateSqlAsync(userId, messageText);
                        try
                        {
                            var result = await connection.QueryAsync(sql);
                            finalAnswer = await aiService.FormatResponseAsync(userId, messageText, JsonSerializer.Serialize(result), isWhatsApp: false);
                        }
                        catch { finalAnswer = "I encountered an error looking that up."; }
                    }
                    else finalAnswer = aiResponse;

                    await telegramService.SendMessageAsync(userId, finalAnswer, chatId);
                }
                catch (Exception ex)
                {
                    // Use a fallback logger if possible or just suppress
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

            // For setup, we need a user ID. 
            // If this endpoint is open, it's a risk. 
            // Let's assume the calling user is the admin or the owner. 
            // We'll use the user from the token if [Authorize] is added, but this controller doesn't have [Authorize] on class.
            // I'll add [Authorize] to the method and use the UserId.
            
            // Wait, this controller handles Webhooks which must be OPEN (Anonymous).
            // So we cannot put [Authorize] on the CLASS.
            // We must put it on this METHOD.
            
            // However, TelegramController inherits ControllerBase. It doesn't have the UserId helper I added to SettingsController.
            // I need to parse claims manually or add the helper.
            
            // But wait, the UI calls this method. So the UI sends the token.
            // So I can check User.Identity.IsAuthenticated.
            
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
