using Dapper;
using GordonWorker.Services;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using System.Security.Claims;
using System.Text.Json;
using Telegram.Bot.Types;

namespace GordonWorker.Controllers;

[ApiController]
[Route("telegram")]
public class TelegramController : ControllerBase
{
    private readonly ITelegramChatService _chatService;
    private readonly ISettingsService _settingsService;
    private readonly ISystemStatusService _statusService;
    private readonly ITelegramService _telegramService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TelegramController> _logger;

    public TelegramController(
        ITelegramChatService chatService,
        ISettingsService settingsService,
        ISystemStatusService statusService,
        ITelegramService telegramService,
        IConfiguration configuration,
        ILogger<TelegramController> logger)
    {
        _chatService = chatService;
        _settingsService = settingsService;
        _statusService = statusService;
        _telegramService = telegramService;
        _configuration = configuration;
        _logger = logger;
    }

    [HttpPost("webhook")]
    public async Task<IActionResult> Webhook([FromBody] JsonElement rawUpdate)
    {
        _statusService.LastTelegramHit = DateTime.UtcNow;
        _statusService.LastTelegramError = "";

        try
        {
            var update = JsonSerializer.Deserialize<Update>(rawUpdate.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (update == null) return Ok();

            string? chatId = null;
            string? messageText = null;

            if (update.Type == Telegram.Bot.Types.Enums.UpdateType.Message && update.Message != null)
            {
                if (update.Message.From?.IsBot == true) return Ok();
                chatId = update.Message.Chat.Id.ToString();
                messageText = update.Message.Text;
            }
            else if (update.Type == Telegram.Bot.Types.Enums.UpdateType.CallbackQuery && update.CallbackQuery != null)
            {
                chatId = update.CallbackQuery.Message?.Chat.Id.ToString();
                messageText = update.CallbackQuery.Data; // Treat callback data as a command message
            }

            if (string.IsNullOrWhiteSpace(chatId) || string.IsNullOrWhiteSpace(messageText)) return Ok();

            // Find matching user
            using var connection = new NpgsqlConnection(_configuration.GetConnectionString("DefaultConnection"));
            var allUsers = await connection.QueryAsync<int>("SELECT id FROM users");
            int? matchedUserId = null;

            foreach (var uid in allUsers)
            {
                var s = await _settingsService.GetSettingsAsync(uid);
                if (s.TelegramChatId == chatId || (s.TelegramAuthorizedChatIds ?? "").Split(',').Contains(chatId))
                {
                    matchedUserId = uid;
                    break;
                }
            }

            if (matchedUserId == null)
            {
                _logger.LogWarning("Unauthorized Telegram message from Chat ID {ChatId}", chatId);
                return Ok();
            }

            // Enqueue for background processing
            await _chatService.EnqueueMessageAsync(matchedUserId.Value, chatId!, messageText!);

            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing Telegram webhook.");
            _statusService.LastTelegramError = ex.Message;
        }

        return Ok();
    }

    [Microsoft.AspNetCore.Authorization.Authorize]
    [HttpPost("setup-webhook")]
    public async Task<IActionResult> SetupWebhook([FromBody] JsonElement body)
    {
        try
        {
            if (!body.TryGetProperty("Url", out var urlElement)) return BadRequest("Missing Url");
            var url = urlElement.GetString();
            if (string.IsNullOrWhiteSpace(url)) return BadRequest("Url empty");

            if (User.Identity?.IsAuthenticated != true) return Unauthorized();
            var userId = int.Parse(User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier)!);

            await _telegramService.InstallWebhookAsync(userId, url);
            return Ok(new { Message = "Webhook registered successfully." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to setup Telegram webhook.");
            return StatusCode(500, new { Error = ex.Message });
        }
    }
}
