using GordonWorker.Models;
using GordonWorker.Repositories;
using GordonWorker.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System.Security.Claims;

namespace GordonWorker.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class TransactionsController : ControllerBase
{
    private readonly ITransactionClassifierService _classifierService;
    private readonly ITransactionRepository _transactionRepository;
    private readonly ILogger<TransactionsController> _logger;

    public TransactionsController(
        ITransactionClassifierService classifierService, 
        ITransactionRepository transactionRepository,
        ILogger<TransactionsController> logger)
    {
        _classifierService = classifierService;
        _transactionRepository = transactionRepository;
        _logger = logger;
    }

    private int UserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpPost("categorize-all")]
    public async Task<IActionResult> CategorizeAll()
    {
        try
        {
            var transactions = (await _transactionRepository.GetTransactionsForCategorizationAsync(UserId)).ToList();

            if (!transactions.Any())
            {
                return Ok(new { Message = "All transactions are already categorized." });
            }

            _logger.LogInformation("Starting batch categorization for {Count} transactions for user {UserId}", transactions.Count, UserId);

            var categorized = await _classifierService.CategorizeTransactionsAsync(UserId, transactions);

            // Update in the repository
            await _transactionRepository.UpdateTransactionsAsync(categorized);

            return Ok(new { Message = $"Successfully categorized {categorized.Count} transactions." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to categorize history for user {UserId}", UserId);
            return StatusCode(500, new { Error = ex.Message });
        }
    }

    [HttpGet]
    public async Task<IActionResult> GetTransactions([FromQuery] int limit = 500)
    {
        try
        {
            var transactions = await _transactionRepository.GetTransactionsByUserAsync(UserId, limit);
            return Ok(transactions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get raw transactions for user {UserId}", UserId);
            return StatusCode(500, new { Error = ex.Message });
        }
    }
}
