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
        var settings = await _settingsService.GetSettingsAsync();

        // 1. Authorization Check
        if (!string.IsNullOrWhiteSpace(settings.AuthorizedWhatsAppNumber) && From != settings.AuthorizedWhatsAppNumber)
        {
            _logger.LogWarning("Unauthorized WhatsApp message from {From}", From);
            return Ok(); // Return 200 to Twilio so it doesn't retry, but ignore message
        }

        _logger.LogInformation("WhatsApp message from {From}: {Body}", From, Body);

        try
        {
            // 2. Fetch Financial Summary (Health Report)
            var summary = await GetFinancialSummaryAsync();
            var summaryJson = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });

            // 3. Try to answer using Summary first
            var promptForSummary = $@"Use the provided financial summary to answer the user's question.
If the summary does NOT contain the specific information needed (e.g. specific transaction details not in the top categories), respond with EXACTLY and ONLY the word 'NEED_SQL'.

USER QUESTION:
{Body}";

            var aiResponse = await _aiService.FormatResponseAsync(promptForSummary, summaryJson);

            string finalAnswer;

            if (aiResponse.Trim().Equals("NEED_SQL", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Summary insufficient. Triggering Text-to-SQL.");
                
                // 4. Generate SQL if summary is not enough
                var sql = await _aiService.GenerateSqlAsync(Body);
                _logger.LogInformation("Generated SQL: {Sql}", sql);

                using var connection = new NpgsqlConnection(_configuration.GetConnectionString("DefaultConnection"));
                await connection.OpenAsync();

                try
                {
                    if (!sql.Trim().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
                    {
                        finalAnswer = "I'm sorry, I couldn't generate a valid query to find that information.";
                    }
                    else
                    {
                        var result = await connection.QueryAsync(sql);
                        var dataContext = JsonSerializer.Serialize(result);
                        finalAnswer = await _aiService.FormatResponseAsync(Body, dataContext);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error executing generated SQL for WhatsApp.");
                    finalAnswer = "I tried to look that up in the database but encountered an error.";
                }
            }
            else
            {
                finalAnswer = aiResponse;
            }

            // 5. Send response back via WhatsApp
            await _twilioService.SendWhatsAppMessageAsync(From, finalAnswer);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing WhatsApp message.");
            await _twilioService.SendWhatsAppMessageAsync(From, "I'm sorry, I encountered an internal error while processing your request.");
        }

        return Ok();
    }

    private async Task<FinancialHealthReport> GetFinancialSummaryAsync()
    {
        var settings = await _settingsService.GetSettingsAsync();
        
        // Fetch last 90 days for analysis
        using var connection = new NpgsqlConnection(_configuration.GetConnectionString("DefaultConnection"));
        await connection.OpenAsync();

        var sqlHistory = "SELECT * FROM transactions WHERE transaction_date >= NOW() - INTERVAL '90 days' ORDER BY transaction_date ASC";
        var history = (await connection.QueryAsync<Transaction>(sqlHistory)).ToList();

        // Get current balance
        var accounts = await _investecClient.GetAccountsAsync();
        decimal currentBalance = 0;
        foreach (var acc in accounts)
        {
            currentBalance += await _investecClient.GetAccountBalanceAsync(acc.AccountId);
        }

        return await _actuarialService.AnalyzeHealthAsync(history, currentBalance);
    }
}
