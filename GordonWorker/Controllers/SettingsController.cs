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
        
        // SECURITY FIX: Mask sensitive fields before sending to frontend
        var response = MaskSettings(settings);
        return Ok(response);
    }

    private AppSettings MaskSettings(AppSettings settings)
    {
        // Clone settings to avoid affecting the service-level cache
        var clone = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        
        const string mask = "********";
        if (!string.IsNullOrEmpty(clone.GeminiApiKey)) clone.GeminiApiKey = mask;
        if (!string.IsNullOrEmpty(clone.FallbackGeminiApiKey)) clone.FallbackGeminiApiKey = mask;
        if (!string.IsNullOrEmpty(clone.ThinkingGeminiApiKey)) clone.ThinkingGeminiApiKey = mask;
        if (!string.IsNullOrEmpty(clone.InvestecSecret)) clone.InvestecSecret = mask;
        if (!string.IsNullOrEmpty(clone.InvestecApiKey)) clone.InvestecApiKey = mask;
        if (!string.IsNullOrEmpty(clone.SmtpPass)) clone.SmtpPass = mask;
        if (!string.IsNullOrEmpty(clone.TwilioAuthToken)) clone.TwilioAuthToken = mask;
        if (!string.IsNullOrEmpty(clone.InvestecClientId)) clone.InvestecClientId = mask;
        if (!string.IsNullOrEmpty(clone.TwilioAccountSid)) clone.TwilioAccountSid = mask;
        if (!string.IsNullOrEmpty(clone.TelegramBotToken)) clone.TelegramBotToken = mask;

        return clone;
    }

    [HttpPut]
    public async Task<IActionResult> Update([FromBody] AppSettings requestSettings)
    {
        try
        {
            var existingSettings = await _settingsService.GetSettingsAsync(UserId);
            
            // SECURITY FIX: Only update sensitive fields if they aren't masked
            const string mask = "********";
            if (requestSettings.GeminiApiKey == mask) requestSettings.GeminiApiKey = existingSettings.GeminiApiKey;
            if (requestSettings.FallbackGeminiApiKey == mask) requestSettings.FallbackGeminiApiKey = existingSettings.FallbackGeminiApiKey;
            if (requestSettings.ThinkingGeminiApiKey == mask) requestSettings.ThinkingGeminiApiKey = existingSettings.ThinkingGeminiApiKey;
            if (requestSettings.InvestecSecret == mask) requestSettings.InvestecSecret = existingSettings.InvestecSecret;
            if (requestSettings.InvestecApiKey == mask) requestSettings.InvestecApiKey = existingSettings.InvestecApiKey;
            if (requestSettings.SmtpPass == mask) requestSettings.SmtpPass = existingSettings.SmtpPass;
            if (requestSettings.TwilioAuthToken == mask) requestSettings.TwilioAuthToken = existingSettings.TwilioAuthToken;
            if (requestSettings.InvestecClientId == mask) requestSettings.InvestecClientId = existingSettings.InvestecClientId;
            if (requestSettings.TwilioAccountSid == mask) requestSettings.TwilioAccountSid = existingSettings.TwilioAccountSid;
            if (requestSettings.TelegramBotToken == mask) requestSettings.TelegramBotToken = existingSettings.TelegramBotToken;

            await _settingsService.UpdateSettingsAsync(UserId, requestSettings);
            return Ok(MaskSettings(requestSettings));
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
            var result = await _aiService.TestConnectionAsync(UserId, useFallback: false);
            _statusService.IsAiPrimaryOnline = result.Success;
            _statusService.PrimaryAiError = result.Success ? string.Empty : result.Error;
            
            if (result.Success)
            {
                _statusService.LastAiCheck = DateTime.UtcNow;
                return Ok(new { Message = "Connected to AI Brain successfully." });
            }
            else
            {
                return StatusCode(500, new { Error = result.Error });
            }
        }
        catch (Exception ex)
        {
            _statusService.IsAiPrimaryOnline = false;
            _statusService.PrimaryAiError = ex.Message;
            return StatusCode(500, new { Error = ex.Message });
        }
    }

    [HttpPost("test-fallback-ai")]
    public async Task<IActionResult> TestFallbackAi()
    {
        try
        {
            var result = await _aiService.TestConnectionAsync(UserId, useFallback: true);
            _statusService.IsAiFallbackOnline = result.Success;
            _statusService.FallbackAiError = result.Success ? string.Empty : result.Error;

            if (result.Success)
            {
                _statusService.LastAiCheck = DateTime.UtcNow;
                return Ok(new { Message = "Connected to Fallback AI Brain successfully." });
            }
            else
            {
                return StatusCode(500, new { Error = result.Error });
            }
        }
        catch (Exception ex)
        {
            _statusService.IsAiFallbackOnline = false;
            _statusService.FallbackAiError = ex.Message;
            return StatusCode(500, new { Error = ex.Message });
        }
    }

    [HttpPost("test-thinking-ai")]
    public async Task<IActionResult> TestThinkingAi()
    {
        try
        {
            var result = await _aiService.TestConnectionAsync(UserId, useFallback: false, useThinking: true);
            if (result.Success)
            {
                return Ok(new { Message = "Connected to Thinking AI successfully." });
            }
            else
            {
                return StatusCode(500, new { Error = result.Error });
            }
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

    [HttpPost("models")]
    public async Task<IActionResult> GetModels([FromBody] AppSettings? settings = null, [FromQuery] bool useFallback = false, [FromQuery] bool useThinking = false)
    {
        // If settings are provided in the body, use those. Otherwise load from DB.
        if (settings != null)
        {
            var dbSettings = await _settingsService.GetSettingsAsync(UserId);
            const string mask = "********";
            if (settings.GeminiApiKey == mask) settings.GeminiApiKey = dbSettings.GeminiApiKey;
            if (settings.FallbackGeminiApiKey == mask) settings.FallbackGeminiApiKey = dbSettings.FallbackGeminiApiKey;
            if (settings.ThinkingGeminiApiKey == mask) settings.ThinkingGeminiApiKey = dbSettings.ThinkingGeminiApiKey;

            var models = await _aiService.GetAvailableModelsAsync(UserId, useFallback, useThinking, settings);
            return Ok(models);
        }
        else
        {
            var models = await _aiService.GetAvailableModelsAsync(UserId, useFallback, useThinking);
            return Ok(models);
        }
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

        // Proactive AI check: if global status is offline, but THIS user might have a working connection,
        // we check it now to provide immediate feedback on dashboard refresh.
        // We only do this if it's currently offline to avoid overhead, and we add a cooldown for SUCCESSFUL checks.
        // If it's currently offline, we ALWAYS allow one re-check to let the user "fix" it by refreshing.
        var canRetryCheck = (DateTime.UtcNow - _statusService.LastAiCheck).TotalMinutes > 1;

        var isPrimaryOnline = _statusService.IsAiPrimaryOnline;
        var primaryError = _statusService.PrimaryAiError;

        if (!isPrimaryOnline || canRetryCheck)
        {
            var (ok, err) = await _aiService.TestConnectionAsync(UserId, useFallback: false);
            isPrimaryOnline = ok;
            primaryError = ok ? string.Empty : err;

            // Only update global status if the current user is an admin or the designated status user
            if (User.IsInRole("Admin"))
            {
                _statusService.IsAiPrimaryOnline = ok;
                _statusService.PrimaryAiError = primaryError;
                _statusService.LastAiCheck = DateTime.UtcNow;
            }
        }

        var isFallbackOnline = _statusService.IsAiFallbackOnline;
        var fallbackError = _statusService.FallbackAiError;
        var canRetryFallback = (DateTime.UtcNow - _statusService.LastAiCheck).TotalMinutes > 1;

        if (settings.EnableAiFallback && (!isFallbackOnline || canRetryFallback))
        {
            var (ok, err) = await _aiService.TestConnectionAsync(UserId, useFallback: true);
            isFallbackOnline = ok;
            fallbackError = ok ? string.Empty : err;

            // Only update global status if the current user is an admin or the designated status user
            if (User.IsInRole("Admin"))
            {
                _statusService.IsAiFallbackOnline = ok;
                _statusService.FallbackAiError = fallbackError;
                _statusService.LastAiCheck = DateTime.UtcNow;
            }
        }

        return Ok(new 
        { 
            InvestecOnline = isInvestecOnline,
            DatabaseOnline = _statusService.IsDatabaseOnline,
            AiPrimaryOnline = isPrimaryOnline,
            AiPrimaryError = primaryError,
            AiFallbackOnline = isFallbackOnline,
            AiFallbackError = fallbackError,
            LastCheck = DateTime.UtcNow,
            LastAiCheck = _statusService.LastAiCheck,
            LastInvestecCheck = _statusService.LastInvestecCheck,
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

    [HttpPost("categorize-existing")]
    public async Task<IActionResult> CategorizeExisting()
    {
        try
        {
            using var connection = new NpgsqlConnection(_configuration.GetConnectionString("DefaultConnection"));
            await connection.OpenAsync();

            var sql = "SELECT * FROM transactions WHERE user_id = @UserId AND is_ai_processed = FALSE LIMIT 50";
            var txs = (await connection.QueryAsync<Transaction>(sql, new { UserId })).ToList();

            if (!txs.Any()) return Ok("No unprocessed transactions found.");

            var categorized = await _aiService.CategorizeTransactionsAsync(UserId, txs);

            foreach (var tx in categorized)
            {
                await connection.ExecuteAsync(
                    "UPDATE transactions SET category = @Category, is_ai_processed = TRUE WHERE id = @Id AND user_id = @UserId",
                    new { tx.Category, tx.Id, UserId });
            }

            return Ok($"Categorized {txs.Count} transactions.");
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Failed to categorize: {ex.Message}");
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

    [HttpGet("stats")]
    public async Task<IActionResult> GetStats()
    {
        try
        {
            var statsJson = await _reportService.GetHealthStatsJsonAsync(UserId);
            return Content(statsJson, "application/json");
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Error = ex.Message });
        }
    }
}
