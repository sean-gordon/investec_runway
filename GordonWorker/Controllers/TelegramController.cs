using Dapper;
using GordonWorker.Models;
using GordonWorker.Services;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using System.Text.Json;
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

        // ... (rest of the webhook logic)
        return Ok();
    }

    [HttpPost("setup-webhook")]
    public async Task<IActionResult> SetupWebhook([FromBody] JsonElement body)
    {
        // ... (existing logic)
        return Ok();
    }

    [HttpGet("webhook-status")]
    public async Task<IActionResult> GetWebhookStatus()
    {
        try
        {
            var info = await _telegramService.GetWebhookInfoAsync();
            return Ok(info);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Error = ex.Message });
        }
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
