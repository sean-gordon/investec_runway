using GordonWorker.Services;
using Microsoft.AspNetCore.Mvc;

namespace GordonWorker.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SettingsController : ControllerBase
{
    private readonly ISettingsService _settingsService;
    private readonly IEmailService _emailService;
    private readonly IOllamaService _ollamaService;
    private readonly IFinancialReportService _reportService;
    private readonly ISystemStatusService _statusService;

    public SettingsController(
        ISettingsService settingsService, 
        IEmailService emailService, 
        IOllamaService ollamaService,
        IFinancialReportService reportService,
        ISystemStatusService statusService)
    {
        _settingsService = settingsService;
        _emailService = emailService;
        _ollamaService = ollamaService;
        _reportService = reportService;
        _statusService = statusService;
    }

    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        return Ok(new 
        { 
            InvestecOnline = _statusService.IsInvestecOnline,
            LastCheck = _statusService.LastInvestecCheck 
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
        var success = await _ollamaService.TestConnectionAsync();
        return success ? Ok("Connected to Ollama successfully.") : StatusCode(500, "Failed to connect to Ollama.");
    }
}
