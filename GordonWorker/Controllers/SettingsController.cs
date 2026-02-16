using GordonWorker.Models;
using GordonWorker.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using System.Text.Json;
using Dapper;
using Npgsql;

namespace GordonWorker.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class SettingsController : ControllerBase
{
    private readonly ISettingsService _settingsService;
    private readonly IEmailService _emailService;
    private readonly IAiService _aiService;
    private readonly IFinancialReportService _reportService;
    private readonly ISystemStatusService _statusService;
    private readonly ITransactionSyncService _syncService;
    private readonly IInvestecClient _investecClient;
    private readonly ITwilioService _twilioService;
    private readonly ITelegramService _telegramService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SettingsController> _logger;

    public SettingsController(
        ISettingsService settingsService, 
        IEmailService emailService, 
        IAiService aiService,
        IFinancialReportService reportService,
        ISystemStatusService statusService,
        ITransactionSyncService syncService,
        IInvestecClient investecClient,
        ITwilioService twilioService,
        ITelegramService telegramService,
        IConfiguration configuration,
        ILogger<SettingsController> logger)
    {
        _settingsService = settingsService;
        _emailService = emailService;
        _aiService = aiService;
        _reportService = reportService;
        _statusService = statusService;
        _syncService = syncService;
        _investecClient = investecClient;
        _twilioService = twilioService;
        _telegramService = telegramService;
        _configuration = configuration;
        _logger = logger;
    }

    private int UserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var settings = await _settingsService.GetSettingsAsync(UserId);
        return Ok(settings);
    }

    [HttpPut]
    public async Task<IActionResult> Update([FromBody] AppSettings settings)
    {
        try
        {
            await _settingsService.UpdateSettingsAsync(UserId, settings);
            return Ok(settings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update settings for user {UserId}", UserId);
            return StatusCode(500, new { Error = ex.Message });
        }
    }

    [HttpPost("test-email")]
    public async Task<IActionResult> TestEmail()
    {
        try
        {
            var success = await _emailService.SendTestEmailAsync(UserId);
            return success ? Ok("Email sent successfully.") : StatusCode(500, "Failed to send email.");
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Error = ex.Message });
        }
    }

    [HttpPost("test-ai")]
    public async Task<IActionResult> TestAi()
    {
        try
        {
            var result = await _aiService.TestConnectionAsync(UserId);
            return result.Success ? Ok(new { Message = "Connected to AI Brain successfully." }) : StatusCode(500, new { Error = result.Error });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Error = ex.Message });
        }
    }

    [HttpPost("test-whatsapp")]
    public async Task<IActionResult> TestWhatsApp()
    {
        var settings = await _settingsService.GetSettingsAsync(UserId);
        if (string.IsNullOrWhiteSpace(settings.AuthorizedWhatsAppNumber)) return BadRequest("WhatsApp Number not configured.");

        await _twilioService.SendWhatsAppMessageAsync(UserId, settings.AuthorizedWhatsAppNumber, "Ping! Multi-user test from Gordon. 🚀");
        return Ok("WhatsApp test message dispatched.");
    }

    [HttpPost("test-telegram")]
    public async Task<IActionResult> TestTelegram()
    {
        var settings = await _settingsService.GetSettingsAsync(UserId);
        if (string.IsNullOrWhiteSpace(settings.TelegramChatId)) return BadRequest("Telegram Chat ID not configured.");

        await _telegramService.SendMessageAsync(UserId, "Ping! Multi-user test from Gordon. 🚀");
        return Ok("Telegram test message dispatched.");
    }

    [HttpPost("test-investec")]
    public async Task<IActionResult> TestInvestec()
    {
        var settings = await _settingsService.GetSettingsAsync(UserId);
        _investecClient.Configure(settings.InvestecClientId, settings.InvestecSecret, settings.InvestecApiKey);
        var (success, error) = await _investecClient.TestConnectivityAsync();
        
        if (success) return Ok("Investec connection successful.");
        return StatusCode(500, $"Investec connection failed: {error}");
    }

    [HttpGet("models")]
    public async Task<IActionResult> GetModels()
    {
        var models = await _aiService.GetAvailableModelsAsync(UserId);
        return Ok(models);
    }

    [HttpGet("status")]
    public async Task<IActionResult> GetStatus()
    {
        // Check Investec status for THIS user on demand (lightweight check)
        var settings = await _settingsService.GetSettingsAsync(UserId);
        var isInvestecOnline = false;
        if (!string.IsNullOrEmpty(settings.InvestecClientId))
        {
            _investecClient.Configure(settings.InvestecClientId, settings.InvestecSecret, settings.InvestecApiKey);
            var (success, _) = await _investecClient.TestConnectivityAsync();
            isInvestecOnline = success;
        }

        return Ok(new 
        { 
            InvestecOnline = isInvestecOnline,
            LastCheck = DateTime.UtcNow,
            LastTelegramHit = _statusService.LastTelegramHit,
            LastTelegramError = _statusService.LastTelegramError
        });
    }

    [HttpPost("send-report-now")]
    public async Task<IActionResult> SendReportNow()
    {
        try
        {
            await _reportService.GenerateAndSendReportAsync(UserId);
            return Ok("Financial Report generated and sent.");
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Failed to send report: {ex.Message}");
        }
    }

    [HttpPost("force-repull")]
    public async Task<IActionResult> ForceRepull()
    {
        try
        {
            await _syncService.ForceRepullAsync(UserId);
            return Ok("Repull initiated.");
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Failed to repull data: {ex.Message}");
        }
    }

    [HttpGet("recent-transactions")]
    public async Task<IActionResult> GetRecentTransactions()
    {
        using var connection = new Npgsql.NpgsqlConnection(_configuration.GetConnectionString("DefaultConnection"));
        var sql = "SELECT * FROM transactions WHERE user_id = @UserId ORDER BY transaction_date DESC LIMIT 10";
        var txs = await connection.QueryAsync<Transaction>(sql, new { UserId });
        return Ok(txs);
    }
}
