using GordonWorker.Services;
using Microsoft.AspNetCore.Mvc;

namespace GordonWorker.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SettingsController : ControllerBase
{
    private readonly ISettingsService _settingsService;
    private readonly IEmailService _emailService;
    private readonly IAiService _ollamaService;
    private readonly IFinancialReportService _reportService;
    private readonly ISystemStatusService _statusService;
    private readonly ITransactionSyncService _syncService;

    private readonly IInvestecClient _investecClient;

    public SettingsController(
        ISettingsService settingsService, 
        IEmailService emailService, 
        IAiService ollamaService,
        IFinancialReportService reportService,
        ISystemStatusService statusService,
        ITransactionSyncService syncService,
        IInvestecClient investecClient)
    {
        _settingsService = settingsService;
        _emailService = emailService;
        _ollamaService = ollamaService;
        _reportService = reportService;
        _statusService = statusService;
        _syncService = syncService;
        _investecClient = investecClient;
    }

    [HttpGet("debug-investec")]
    public async Task<IActionResult> DebugInvestec()
    {
        try
        {
            var accounts = await _investecClient.GetAccountsAsync();
            var debugData = new Dictionary<string, object>
            {
                { "AccountsFound", accounts.Count },
                { "Accounts", accounts }
            };

            foreach (var acc in accounts)
            {
                var txs = await _investecClient.GetTransactionsAsync(acc.AccountId, DateTimeOffset.UtcNow.AddDays(-30));
                debugData.Add($"Transactions_{acc.AccountId}", txs);
            }

            return Ok(debugData);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Error = ex.Message, Stack = ex.StackTrace });
        }
    }

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var settings = await _settingsService.GetSettingsAsync();
        return Ok(settings);
    }

    [HttpPut]
    public async Task<IActionResult> Update([FromBody] AppSettings settings)
    {
        await _settingsService.UpdateSettingsAsync(settings);
        return Ok(settings);
    }

    [HttpPost("test-email")]
    public async Task<IActionResult> TestEmail()
    {
        var success = await _emailService.SendTestEmailAsync();
        return success ? Ok("Email sent successfully.") : StatusCode(500, "Failed to send email. Check logs.");
    }

    [HttpPost("test-ai")]
    public async Task<IActionResult> TestAi()
    {
        var result = await _ollamaService.TestConnectionAsync();
        return result.Success ? Ok("Connected to AI Brain successfully.") : StatusCode(500, result.Error);
    }

    [HttpPost("test-whatsapp")]
    public async Task<IActionResult> TestWhatsApp()
    {
        var settings = await _settingsService.GetSettingsAsync();
        if (string.IsNullOrWhiteSpace(settings.AuthorizedWhatsAppNumber))
        {
            return BadRequest("Authorized WhatsApp Number is not configured.");
        }

        var twilioService = HttpContext.RequestServices.GetRequiredService<ITwilioService>();
        await twilioService.SendWhatsAppMessageAsync(settings.AuthorizedWhatsAppNumber, "Ping! This is a test message from your Gordon Finance Engine. 🚀");
        return Ok("WhatsApp test message dispatched.");
    }

    [HttpGet("models")]
    public async Task<IActionResult> GetModels()
    {
        var models = await _ollamaService.GetAvailableModelsAsync();
        return Ok(models);
    }

    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        return Ok(new 
        { 
            InvestecOnline = _statusService.IsInvestecOnline,
            LastCheck = _statusService.LastInvestecCheck,
            LastError = _statusService.LastError
        });
    }

    [HttpPost("send-report-now")]
    public async Task<IActionResult> SendReportNow()
    {
        try
        {
            await _reportService.GenerateAndSendReportAsync();
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
            await _syncService.ForceRepullAsync();
            return Ok("Repull initiated. Check logs for progress.");
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Failed to repull data: {ex.Message}");
        }
    }
}