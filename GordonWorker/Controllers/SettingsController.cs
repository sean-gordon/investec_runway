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

    public SettingsController(ISettingsService settingsService, IEmailService emailService, IOllamaService ollamaService)
    {
        _settingsService = settingsService;
        _emailService = emailService;
        _ollamaService = ollamaService;
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
