using Dapper;
using GordonWorker.Models;
using GordonWorker.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using System.Security.Claims;
using System.Linq;

namespace GordonWorker.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class TransactionsController : ControllerBase
{
    private readonly IAiService _aiService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TransactionsController> _logger;

    public TransactionsController(IAiService aiService, IConfiguration configuration, ILogger<TransactionsController> logger)
    {
        _aiService = aiService;
        _configuration = configuration;
        _logger = logger;
    }

    [HttpPost("categorize-all")]
    public async Task<IActionResult> CategorizeAll()
    {
        var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(userIdStr, out var userId)) return Unauthorized();

        try
        {
            using var connection = new NpgsqlConnection(_configuration.GetConnectionString("DefaultConnection"));
            // Find transactions that are not AI processed OR have no category
            var transactions = (await connection.QueryAsync<Transaction>(
                "SELECT * FROM transactions WHERE user_id = @userId AND (is_ai_processed = false OR category IS NULL OR category = '')", 
                new { userId })).ToList();

            if (!transactions.Any())
            {
                return Ok(new { Message = "All transactions are already categorized." });
            }

            _logger.LogInformation("Starting batch categorization for {Count} transactions for user {UserId}", transactions.Count, userId);

            // The AiService already handles batching internally (batchSize=50) to avoid overloading AI limits
            var categorized = await _aiService.CategorizeTransactionsAsync(userId, transactions);

            // Update the database in batches to avoid locking issues
            const int updateBatchSize = 100;
            for (int i = 0; i < categorized.Count; i += updateBatchSize)
            {
                var batchToSave = categorized.Skip(i).Take(updateBatchSize).ToList();
                var sql = "UPDATE transactions SET category = @Category, is_ai_processed = true WHERE id = @Id AND user_id = @UserId";
                await connection.ExecuteAsync(sql, batchToSave.Select(t => new { t.Category, t.Id, UserId = userId }));
            }

            return Ok(new { Message = $"Successfully categorized {categorized.Count} transactions." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to categorize history for user {UserId}", userId);
            return StatusCode(500, new { Error = ex.Message });
        }
    }

    [HttpGet]
    public async Task<IActionResult> GetTransactions([FromQuery] int limit = 500)
    {
        var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(userIdStr, out var userId)) return Unauthorized();

        try
        {
            using var connection = new NpgsqlConnection(_configuration.GetConnectionString("DefaultConnection"));
            var sql = "SELECT * FROM transactions WHERE user_id = @userId ORDER BY transaction_date DESC LIMIT @limit";
            var transactions = await connection.QueryAsync<Transaction>(sql, new { userId, limit });
            
            return Ok(transactions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get raw transactions for user {UserId}", userId);
            return StatusCode(500, new { Error = ex.Message });
        }
    }
}
