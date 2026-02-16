using GordonWorker.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace GordonWorker.Controllers;

[Authorize]
[ApiController]
[Route("[controller]")]
public class ChatController : ControllerBase
{
    private readonly IAiService _aiService;
    private readonly ISettingsService _settingsService;
    private readonly ILogger<ChatController> _logger;

    private readonly IFinancialReportService _reportService;

    public ChatController(IAiService aiService, ISettingsService settingsService, IFinancialReportService reportService, ILogger<ChatController> logger)
    {
        _aiService = aiService;
        _settingsService = settingsService;
        _reportService = reportService;
        _logger = logger;
    }

    private int UserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpPost]
    public async Task<IActionResult> Post([FromBody] ChatRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Message)) return BadRequest("Message cannot be empty.");

        try
        {
            var financialContext = await _reportService.GetHealthStatsJsonAsync(UserId);
            var response = await _aiService.FormatResponseAsync(UserId, request.Message, financialContext);
            return Ok(new { response });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Chat failed for user {UserId}", UserId);
            return StatusCode(500, "Error processing request.");
        }
    }

    public class ChatRequest { public string Message { get; set; } = ""; }
}
