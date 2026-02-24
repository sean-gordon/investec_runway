using Dapper;
using GordonWorker.Models;
using GordonWorker.Services;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using System.Text.Json;

namespace GordonWorker.Controllers;

[ApiController]
[Route("api/[controller]")]
public class WhatsAppController : ControllerBase
{
    private readonly IAiService _aiService;
    private readonly IActuarialService _actuarialService;
    private readonly ITwilioService _twilioService;
    private readonly ISettingsService _settingsService;
    private readonly IInvestecClient _investecClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<WhatsAppController> _logger;

    public WhatsAppController(
        IAiService aiService,
        IActuarialService actuarialService,
        ITwilioService twilioService,
        ISettingsService settingsService,
        IInvestecClient investecClient,
        IConfiguration configuration,
        ILogger<WhatsAppController> logger)
    {
        _aiService = aiService;
        _actuarialService = actuarialService;
        _twilioService = twilioService;
        _settingsService = settingsService;
        _investecClient = investecClient;
        _configuration = configuration;
        _logger = logger;
    }

    [HttpPost("webhook")]
    [Consumes("application/x-www-form-urlencoded")]
    public async Task<IActionResult> Webhook([FromForm] string From, [FromForm] string Body)
    {
        // 1. Identify User by WhatsApp Number
        int? matchedUserId = null;
        AppSettings? userSettings = null;

        using (var connection = new NpgsqlConnection(_configuration.GetConnectionString("DefaultConnection")))
        {
            await connection.OpenAsync();
            var userIds = await connection.QueryAsync<int>("SELECT id FROM users");
            
            foreach (var uid in userIds)
            {
                var s = await _settingsService.GetSettingsAsync(uid);
                if (s.AuthorizedWhatsAppNumber == From)
                {
                    matchedUserId = uid;
                    userSettings = s;
                    break;
                }
            }
        }

        if (matchedUserId == null || userSettings == null)
        {
            _logger.LogWarning("Unauthorized WhatsApp message from {From}", From);
            return Ok();
        }

        // 1.5 Validate Twilio Signature
        if (!string.IsNullOrWhiteSpace(userSettings.TwilioAuthToken))
        {
            var signature = Request.Headers["X-Twilio-Signature"].ToString();
            var requestUrl = $"{Request.Scheme}://{Request.Host}{Request.Path}{Request.QueryString}";
            var form = await Request.ReadFormAsync();
            var parameters = form.ToDictionary(k => k.Key, v => v.Value.ToString());

            var validator = new Twilio.Security.RequestValidator(userSettings.TwilioAuthToken);
            if (!validator.Validate(requestUrl, parameters, signature))
            {
                _logger.LogWarning("Twilio signature validation failed for user {UserId}", matchedUserId);
            }
        }

        var userId = matchedUserId.Value;
        _logger.LogInformation("WhatsApp message from {From} (User {UserId}): {Body}", From, userId, Body);

        try
        {
            // Handle Slash Commands
            if (Body.Trim().StartsWith("/"))
            {
                var cmd = Body.Trim().Split(' ')[0].ToLower();
                if (cmd == "/clear")
                {
                    await _twilioService.SendWhatsAppMessageAsync(userId, From, "⚠️ *Warning: Clear History*\n\nThis will permanently delete your entire conversation history with the AI. This action cannot be undone.\n\nReply with */clear_confirm* to proceed, or any other message to cancel.");
                    return Ok();
                }
                if (cmd == "/clear_confirm")
                {
                    using (var dbConfirm = new NpgsqlConnection(_configuration.GetConnectionString("DefaultConnection")))
                    {
                        await dbConfirm.ExecuteAsync("DELETE FROM chat_history WHERE user_id = @UserId", new { UserId = userId });
                    }
                    await _twilioService.SendWhatsAppMessageAsync(userId, From, "✅ *Success:* Your conversation history has been cleared.");
                    return Ok();
                }
                if (cmd == "/model")
                {
                    await _twilioService.SendWhatsAppMessageAsync(userId, From, "⚙️ *AI Model Configuration*\n\nWhich provider would you like to configure?\n\n1. Primary AI\n2. Backup AI\n\nReply with */model_1* or */model_2*.");
                    return Ok();
                }
                if (cmd == "/model_1" || cmd == "/model_2")
                {
                    bool isPrimary = cmd == "/model_1";
                    var provider = isPrimary ? userSettings.AiProvider : userSettings.FallbackAiProvider;
                    await _twilioService.SendWhatsAppMessageAsync(userId, From, $"⚙️ *Select Provider for {(isPrimary ? "Primary" : "Backup")}*\n\nCurrent: _{provider}_\n\n1. Ollama (Local)\n2. Gemini (Cloud)\n\nReply with */prov_{(isPrimary ? "1" : "2")}_ollama* or */prov_{(isPrimary ? "1" : "2")}_gemini*.");
                    return Ok();
                }
                if (cmd.StartsWith("/prov_")) // e.g. /prov_1_ollama
                {
                    var parts = cmd.Split('_');
                    bool isPrimary = parts[1] == "1";
                    var provider = parts[2];
                    
                    if (provider == "gemini") {
                        var current = await _settingsService.GetSettingsAsync(userId);
                        if (isPrimary) current.AiProvider = "Gemini"; else current.FallbackAiProvider = "Gemini";
                        await _settingsService.UpdateSettingsAsync(userId, current);
                        await _twilioService.SendWhatsAppMessageAsync(userId, From, $"✅ *Success:* {(isPrimary ? "Primary" : "Backup")} AI updated to *Gemini API*.");
                    } else {
                        // Create temporary settings to fetch models for OLLAMA
                        var tempSettings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(userSettings))!;
                        if (isPrimary) tempSettings.AiProvider = "Ollama"; else tempSettings.FallbackAiProvider = "Ollama";

                        var models = await _aiService.GetAvailableModelsAsync(userId, !isPrimary, false, tempSettings);
                        var menu = $"⚙️ *Select Ollama Model ({(isPrimary ? "Primary" : "Backup")})*\n\n";
                        for(int i=0; i<Math.Min(models.Count, 9); i++) {
                            menu += $"{i+1}. {models[i]}\n";
                        }
                        menu += "\nReply with */set_" + (isPrimary ? "1" : "2") + "_[number]* (e.g. */set_1_1*)";
                        await _twilioService.SendWhatsAppMessageAsync(userId, From, menu);
                    }
                    return Ok();
                }
                if (cmd.StartsWith("/set_")) // e.g. /set_1_1
                {
                    var parts = cmd.Split('_');
                    bool isPrimary = parts[1] == "1";
                    int modelIndex = 0;
                    if (int.TryParse(parts[2], out int idx)) modelIndex = idx - 1;
                    
                    // Create temporary settings to fetch models for OLLAMA to verify index
                    var tempSettings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(userSettings))!;
                    if (isPrimary) tempSettings.AiProvider = "Ollama"; else tempSettings.FallbackAiProvider = "Ollama";

                    var models = await _aiService.GetAvailableModelsAsync(userId, !isPrimary, false, tempSettings);
                    
                    if (modelIndex >= 0 && modelIndex < models.Count) {
                        var modelName = models[modelIndex];
                        var current = await _settingsService.GetSettingsAsync(userId);
                        if (isPrimary) {
                            current.AiProvider = "Ollama";
                            current.OllamaModelName = modelName;
                        } else {
                            current.FallbackAiProvider = "Ollama";
                            current.FallbackOllamaModelName = modelName;
                        }
                        await _settingsService.UpdateSettingsAsync(userId, current);
                        await _twilioService.SendWhatsAppMessageAsync(userId, From, $"✅ *Success:* {(isPrimary ? "Primary" : "Backup")} AI updated to *{modelName}* (Ollama).");
                    }
                    return Ok();
                }
            }

            var summary = await GetFinancialSummaryAsync(userId);
            var summaryJson = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });

            var promptForSummary = $@"Use the provided financial summary to answer the user's question.
If the summary does NOT contain the specific information needed, respond with EXACTLY 'NEED_SQL'.
USER QUESTION: {Body}";

            var aiResponse = await _aiService.FormatResponseAsync(userId, promptForSummary, summaryJson, isWhatsApp: true);
            string finalAnswer;

            if (string.IsNullOrWhiteSpace(aiResponse))
            {
                finalAnswer = "I'm currently experiencing some latency in my analytical engine. Please try again in a few minutes.";
            }
            else if (aiResponse.Trim().Equals("NEED_SQL", StringComparison.OrdinalIgnoreCase))
            {
                // Temporarily disabled SQL generation for multi-tenant safety or ensure it uses userId filter
                // Ideally: var sql = await _aiService.GenerateSqlAsync(userId, Body);
                // But safer to just say unavailable for now or implement safe RLS
                finalAnswer = "I'm sorry, deep database search is temporarily disabled for security upgrades. Please ask about the summary data.";
            }
            else
            {
                finalAnswer = aiResponse;
            }

            await _twilioService.SendWhatsAppMessageAsync(userId, From, finalAnswer);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing WhatsApp message.");
            await _twilioService.SendWhatsAppMessageAsync(userId, From, "I'm sorry, I encountered an internal error.");
        }

        return Ok();
    }

    private async Task<FinancialHealthReport> GetFinancialSummaryAsync(int userId)
    {
        var settings = await _settingsService.GetSettingsAsync(userId);
        _investecClient.Configure(settings.InvestecClientId, settings.InvestecSecret, settings.InvestecApiKey);

        using var connection = new NpgsqlConnection(_configuration.GetConnectionString("DefaultConnection"));
        var history = (await connection.QueryAsync<Transaction>(
            "SELECT * FROM transactions WHERE user_id = @userId AND transaction_date >= NOW() - INTERVAL '90 days' ORDER BY transaction_date ASC", 
            new { userId })).ToList();

        var accounts = await _investecClient.GetAccountsAsync();
        decimal currentBalance = 0;
        foreach (var acc in accounts)
        {
            currentBalance += await _investecClient.GetAccountBalanceAsync(acc.AccountId);
        }

        return await _actuarialService.AnalyzeHealthAsync(history, currentBalance, settings);
    }
}
