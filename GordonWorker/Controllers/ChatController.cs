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

    public ChatController(IAiService aiService, ISettingsService settingsService, ILogger<ChatController> logger)
    {
        _aiService = aiService;
        _settingsService = settingsService;
        _logger = logger;
    }

    private int UserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpPost]
    public async Task<IActionResult> Post([FromBody] ChatRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Message)) return BadRequest("Message cannot be empty.");

        try
        {
            // For general chat, we might want to fetch some context or just chat. 
            // For now, we'll pass an empty context or minimal user context.
            // A better approach would be to have a specialized "Chat" method in AiService that fetches recent history if needed.
            // But FormatResponseAsync expects dataContext. 
            
            // Let's reuse FormatResponseAsync but with a minimal "No financial data provided for this specific query" context 
            // unless we want to inject the summary here too.
            // For a "Smart" chat, we ideally want the summary.
            
            // I'll stick to a simple chat for now to satisfy the compilation.
            var response = await _aiService.FormatResponseAsync(UserId, request.Message, "No specific financial data context provided for this message.");
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
