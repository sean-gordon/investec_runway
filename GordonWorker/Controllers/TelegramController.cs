using Dapper;
using GordonWorker.Models;
using GordonWorker.Services;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using System.Text.Json;
using Telegram.Bot.Types;

namespace GordonWorker.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TelegramController : ControllerBase
{
    private readonly IAiService _aiService;
    private readonly IActuarialService _actuarialService;
    private readonly ITelegramService _telegramService;
    private readonly ISettingsService _settingsService;
    private readonly IInvestecClient _investecClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TelegramController> _logger;

    public TelegramController(
        IAiService aiService,
        IActuarialService actuarialService,
        ITelegramService telegramService,
        ISettingsService settingsService,
        IInvestecClient investecClient,
        IConfiguration configuration,
        ILogger<TelegramController> logger)
    {
        _aiService = aiService;
        _actuarialService = actuarialService;
        _telegramService = telegramService;
        _settingsService = settingsService;
        _investecClient = investecClient;
        _configuration = configuration;
        _logger = logger;
    }

    [HttpPost("webhook")]
    public async Task<IActionResult> Webhook([FromBody] Update update)
    {
        if (update.Message == null || string.IsNullOrWhiteSpace(update.Message.Text))
            return Ok();

        var settings = await _settingsService.GetSettingsAsync();
        var chatId = update.Message.Chat.Id.ToString();
        var messageText = update.Message.Text;

        // 1. Authorization Check
        if (!string.IsNullOrWhiteSpace(settings.TelegramChatId) && chatId != settings.TelegramChatId)
        {
            _logger.LogWarning("Unauthorized Telegram message from Chat ID {ChatId}", chatId);
            return Ok(); 
        }

        _logger.LogInformation("Telegram message from {ChatId}: {Body}", chatId, messageText);

        try
        {
            // 2. Fetch Financial Summary (Health Report)
            var summary = await GetFinancialSummaryAsync();
            var summaryJson = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });

            // 3. Try to answer using Summary first
            var promptForSummary = $@"Use the provided financial summary to answer the user's question.
Keep the response concise as it's being sent via Telegram.
If the summary does NOT contain the specific information needed (e.g. specific transaction details not in the top categories), respond with EXACTLY and ONLY the word 'NEED_SQL'.

USER QUESTION:
{messageText}";

            var aiResponse = await _aiService.FormatResponseAsync(promptForSummary, summaryJson, isWhatsApp: false);

            string finalAnswer;

            if (aiResponse.Trim().Equals("NEED_SQL", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Summary insufficient. Triggering Text-to-SQL.");
                
                // 4. Generate SQL if summary is not enough
                var sql = await _aiService.GenerateSqlAsync(messageText);
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
                        finalAnswer = await _aiService.FormatResponseAsync(messageText, dataContext, isWhatsApp: false);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error executing generated SQL for Telegram.");
                    finalAnswer = "I tried to look that up in the database but encountered an error.";
                }
            }
            else
            {
                finalAnswer = aiResponse;
            }

            // 5. Send response back via Telegram
            await _telegramService.SendMessageAsync(finalAnswer);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing Telegram message.");
            await _telegramService.SendMessageAsync("I'm sorry, I encountered an internal error while processing your request.");
        }

        return Ok();
    }

    private async Task<FinancialHealthReport> GetFinancialSummaryAsync()
    {
        var settings = await _settingsService.GetSettingsAsync();
        
        using var connection = new NpgsqlConnection(_configuration.GetConnectionString("DefaultConnection"));
        await connection.OpenAsync();

        var sqlHistory = "SELECT * FROM transactions WHERE transaction_date >= NOW() - INTERVAL '90 days' ORDER BY transaction_date ASC";
        var history = (await connection.QueryAsync<Transaction>(sqlHistory)).ToList();

        var accounts = await _investecClient.GetAccountsAsync();
        decimal currentBalance = 0;
        foreach (var acc in accounts)
        {
            currentBalance += await _investecClient.GetAccountBalanceAsync(acc.AccountId);
        }

        return await _actuarialService.AnalyzeHealthAsync(history, currentBalance);
    }
}
