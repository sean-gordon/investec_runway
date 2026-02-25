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
public class ChartDataController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly IActuarialService _actuarialService;
    private readonly ISettingsService _settingsService;
    private readonly IInvestecClient _investecClient;

    public ChartDataController(IConfiguration configuration, IActuarialService actuarialService, ISettingsService settingsService, IInvestecClient investecClient)
    {
        _configuration = configuration;
        _actuarialService = actuarialService;
        _settingsService = settingsService;
        _investecClient = investecClient;
    }

    [HttpGet("spending-by-category")]
    public async Task<IActionResult> GetSpendingByCategory([FromQuery] int days = 30)
    {
        var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(userIdStr, out var userId)) return Unauthorized();

        using var connection = new NpgsqlConnection(_configuration.GetConnectionString("DefaultConnection"));
        var sql = @"
            SELECT COALESCE(category, 'Uncategorized') AS Label, SUM(amount) AS Value 
            FROM transactions 
            WHERE user_id = @userId AND amount > 0 AND transaction_date >= NOW() - INTERVAL '1 day' * @days
            GROUP BY category
            ORDER BY Value DESC";

        var data = await connection.QueryAsync(sql, new { userId, days });
        return Ok(data);
    }

    [HttpGet("daily-balance")]
    public async Task<IActionResult> GetDailyBalance([FromQuery] int days = 30)
    {
        var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(userIdStr, out var userId)) return Unauthorized();

        var settings = await _settingsService.GetSettingsAsync(userId);
        using var connection = new NpgsqlConnection(_configuration.GetConnectionString("DefaultConnection"));
        
        var history = (await connection.QueryAsync<Transaction>(
            "SELECT * FROM transactions WHERE user_id = @userId AND transaction_date >= NOW() - INTERVAL '1 day' * @days ORDER BY transaction_date DESC", 
            new { userId, days })).ToList();

        _investecClient.Configure(settings.InvestecClientId, settings.InvestecSecret, settings.InvestecApiKey);
        var accounts = await _investecClient.GetAccountsAsync();
        var balances = await Task.WhenAll(accounts.Select(a => _investecClient.GetAccountBalanceAsync(a.AccountId)));
        decimal currentBalance = balances.Sum();

        var points = new List<DailyBalancePoint>();
        decimal runner = currentBalance;
        points.Add(new DailyBalancePoint { Date = DateTimeOffset.UtcNow, Balance = runner });

        foreach (var tx in history)
        {
            runner += tx.Amount; // Reverse the transaction to go back in time
            points.Add(new DailyBalancePoint { Date = tx.TransactionDate, Balance = runner });
        }

        return Ok(points.OrderBy(p => p.Date));
    }

    public class DailyBalancePoint
    {
        public DateTimeOffset Date { get; set; }
        public decimal Balance { get; set; }
    }
}
