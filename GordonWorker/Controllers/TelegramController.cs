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
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _tokenCache = new();

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

    [HttpPost("webhook/{token}")]
    public async Task<IActionResult> Webhook(string token, [FromBody] JsonElement rawUpdate)
    {
        _statusService.LastTelegramHit = DateTime.UtcNow;
        _statusService.LastTelegramError = "";

        try
        {
            var json = rawUpdate.GetRawText();
            _logger.LogDebug("Telegram Webhook Raw: {Json}", json);

            var update = JsonSerializer.Deserialize<Update>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (update == null) 
            {
                _logger.LogWarning("Telegram update deserialized to null.");
                return Ok();
            }

            string? chatId = null;
            string? messageText = null;

            if (update.Message != null)
            {
                if (update.Message.From?.IsBot == true) return Ok();
                chatId = update.Message.Chat.Id.ToString();
                messageText = update.Message.Text;
            }
            else if (update.CallbackQuery != null)
            {
                chatId = update.CallbackQuery.Message?.Chat.Id.ToString();
                messageText = update.CallbackQuery.Data;
            }

            if (string.IsNullOrWhiteSpace(chatId) || string.IsNullOrWhiteSpace(messageText)) return Ok();

            // SECURITY FIX: Find matching user and VERIFY TOKEN
            int? matchedUserId = null;
            Models.AppSettings? matchedSettings = null;

            if (_tokenCache.TryGetValue(token, out var cachedUserId))
            {
                var s = await _settingsService.GetSettingsAsync(cachedUserId);
                var expectedToken = GenerateSecretToken(s.TelegramBotToken ?? "");
                if (token == expectedToken)
                {
                    matchedUserId = cachedUserId;
                    matchedSettings = s;
                }
                else
                {
                    // Token changed, invalidate cache
                    _tokenCache.TryRemove(token, out _);
                }
            }

            if (matchedUserId == null)
            {
                using var connection = new NpgsqlConnection(_configuration.GetConnectionString("DefaultConnection"));
                var allUsers = await connection.QueryAsync<int>("SELECT id FROM users");

                foreach (var uid in allUsers)
                {
                    var s = await _settingsService.GetSettingsAsync(uid);
                    if (string.IsNullOrEmpty(s.TelegramBotToken)) continue;

                    // Simple but effective token verification: SHA256 of BotToken
                    var expectedToken = GenerateSecretToken(s.TelegramBotToken);
                    if (token != expectedToken) continue;

                    matchedUserId = uid;
                    matchedSettings = s;
                    _tokenCache[token] = uid; // Save to cache
                    break;
                }
            }

            if (matchedUserId == null || matchedSettings == null)
            {
                _logger.LogWarning("Unauthorized or mismatched Telegram message. Token not found for Chat ID {ChatId}", chatId);
                return Ok();
            }

            var authorized = (matchedSettings.TelegramAuthorizedChatIds ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (matchedSettings.TelegramChatId != chatId && !authorized.Contains(chatId))
            {
                _logger.LogWarning("Unauthorized chat ID {ChatId} for resolved user {UserId}", chatId, matchedUserId);
                return Ok();
            }

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

    private string GenerateSecretToken(string botToken)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var hash = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(botToken));
        return Convert.ToHexString(hash).ToLower();
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

            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!int.TryParse(userIdStr, out var userId)) return Unauthorized();

            var settings = await _settingsService.GetSettingsAsync(userId);
            if (string.IsNullOrEmpty(settings.TelegramBotToken)) return BadRequest("Bot token not configured.");

            var secretToken = GenerateSecretToken(settings.TelegramBotToken);
            var finalUrl = url.TrimEnd('/') + "/webhook/" + secretToken;

            await _telegramService.InstallWebhookAsync(userId, finalUrl);
            return Ok(new { Message = "Webhook registered successfully (with secret token)." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to setup Telegram webhook.");
            return StatusCode(500, new { Error = ex.Message });
        }
    }
}
