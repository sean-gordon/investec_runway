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
