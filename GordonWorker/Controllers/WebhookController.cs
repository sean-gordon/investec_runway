using GordonWorker.Events;
using GordonWorker.Models;
using GordonWorker.Repositories;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace GordonWorker.Controllers;

/// <summary>
/// Receives real-time card-transaction push notifications from Investec
/// Programmable Banking. Callers must supply the shared secret in the
/// X-Webhook-Secret request header; mismatched or missing secrets return 401.
/// Configure the secret via appsettings.json Webhooks:InvestecSecret or the
/// WEBHOOK_INVESTEC_SECRET environment variable.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class WebhookController : ControllerBase
{
    private readonly ITransactionRepository _transactionRepository;
    private readonly IUserRepository _userRepository;
    private readonly IMediator _mediator;
    private readonly IConfiguration _configuration;
    private readonly ILogger<WebhookController> _logger;

    public WebhookController(
        ITransactionRepository transactionRepository,
        IUserRepository userRepository,
        IMediator mediator,
        IConfiguration configuration,
        ILogger<WebhookController> logger)
    {
        _transactionRepository = transactionRepository;
        _userRepository = userRepository;
        _mediator = mediator;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// POST api/webhook/investec
    /// Investec Programmable Banking pushes a payload here on every card transaction.
    /// Point your Investec webhook URL to: https://&lt;your-host&gt;/api/webhook/investec
    /// </summary>
    [HttpPost("investec")]
    public async Task<IActionResult> InvestecTransaction(
        [FromBody] InvestecWebhookPayload payload,
        CancellationToken cancellationToken)
    {
        if (!ValidateSecret())
        {
            _logger.LogWarning("Investec webhook rejected: bad or missing X-Webhook-Secret from {IP}",
                HttpContext.Connection.RemoteIpAddress);
            return Unauthorized();
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.AccountNumber))
        {
            _logger.LogWarning("Investec webhook: malformed payload received.");
            return BadRequest("Invalid payload.");
        }

        _logger.LogInformation(
            "Investec webhook: account={Account}, cents={Cents}, merchant={Merchant}",
            payload.AccountNumber, payload.CentsAmount, payload.MerchantName ?? payload.Description);

        var users = await _userRepository.GetAllUsersAsync();
        foreach (var user in users)
        {
            var tx = payload.ToTransaction(user.Id);
            var inserted = await _transactionRepository.InsertTransactionsBatchAsync(
                new List<Transaction> { tx }, user.Id);

            if (inserted > 0)
            {
                _logger.LogInformation("Webhook: inserted real-time transaction for user {UserId}.", user.Id);

                await _mediator.Publish(new TransactionsSyncedEvent
                {
                    UserId = user.Id,
                    Transactions = new List<Transaction> { tx },
                    Silent = false,
                    ForceCategorizeAll = false
                }, cancellationToken);
            }
        }

        return Ok();
    }

    // Constant-time secret comparison to prevent timing-based enumeration.
    private bool ValidateSecret()
    {
        var expected = _configuration["Webhooks:InvestecSecret"]
            ?? Environment.GetEnvironmentVariable("WEBHOOK_INVESTEC_SECRET");

        if (string.IsNullOrWhiteSpace(expected)) return false;

        if (!Request.Headers.TryGetValue("X-Webhook-Secret", out var supplied))
            return false;

        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied.ToString());

        if (expectedBytes.Length != suppliedBytes.Length) return false;
        return CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }
}

/// <summary>Investec Programmable Banking real-time card-transaction payload.</summary>
public sealed class InvestecWebhookPayload
{
    [JsonPropertyName("accountNumber")]
    public string AccountNumber { get; set; } = string.Empty;

    [JsonPropertyName("dateTime")]
    public DateTimeOffset DateTime { get; set; }

    /// <summary>Transaction value in cents (always positive; sign is derived from type).</summary>
    [JsonPropertyName("centsAmount")]
    public long CentsAmount { get; set; }

    [JsonPropertyName("currencyCode")]
    public string CurrencyCode { get; set; } = "zar";

    [JsonPropertyName("reference")]
    public string? Reference { get; set; }

    /// <summary>Merchant name from card network (preferred description source).</summary>
    [JsonPropertyName("merchantName")]
    public string? MerchantName { get; set; }

    [JsonPropertyName("merchantCity")]
    public string? MerchantCity { get; set; }

    /// <summary>Flat description field used by some Investec webhook variants.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    public Transaction ToTransaction(int userId)
    {
        // Card push events represent card-present debits — store as negative amounts
        // to match the convention used by the REST API transaction sync.
        var amount = -(CentsAmount / 100m);

        return new Transaction
        {
            Id = Guid.NewGuid(),
            AccountId = AccountNumber,
            TransactionDate = DateTime == default ? DateTimeOffset.UtcNow : DateTime,
            Description = MerchantName ?? Description ?? Reference ?? "Webhook Transaction",
            Amount = amount,
            Balance = 0, // Real-time webhooks do not include the post-transaction balance
            Category = null,
            IsAiProcessed = false
        };
    }
}
