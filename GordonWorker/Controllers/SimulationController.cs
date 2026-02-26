using Dapper;
using GordonWorker.Models;
using GordonWorker.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using System.Security.Claims;

namespace GordonWorker.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class SimulationController : ControllerBase
{
    private readonly IActuarialService _actuarialService;
    private readonly ISettingsService _settingsService;
    private readonly IConfiguration _configuration;
    private readonly IInvestecClient _investecClient;

    public SimulationController(IActuarialService actuarialService, ISettingsService settingsService, IConfiguration configuration, IInvestecClient investecClient)
    {
        _actuarialService = actuarialService;
        _settingsService = settingsService;
        _configuration = configuration;
        _investecClient = investecClient;
    }

    [HttpPost]
    public async Task<IActionResult> RunSimulation([FromBody] SimulationRequest request)
    {
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            return Unauthorized();
        }

        var settings = await _settingsService.GetSettingsAsync(userId);
        var historyDays = settings.HistoryDaysBack > 0 ? settings.HistoryDaysBack : 180;
        
        using var connection = new NpgsqlConnection(_configuration.GetConnectionString("DefaultConnection"));
        var history = (await connection.QueryAsync<Transaction>(
            $"SELECT * FROM transactions WHERE user_id = @userId AND transaction_date >= NOW() - INTERVAL '{historyDays} days'", 
            new { userId })).ToList();
        
        _investecClient.Configure(settings.InvestecClientId, settings.InvestecSecret, settings.InvestecApiKey);
        var accounts = await _investecClient.GetAccountsAsync();
        
        var balanceTasks = accounts.Select(acc => _investecClient.GetAccountBalanceAsync(acc.AccountId));
        var balances = await Task.WhenAll(balanceTasks);
        decimal currentBalance = balances.Sum();

        // Apply Adjustments
        foreach (var adj in request.Adjustments)
        {
            if (adj.Type == "OneOffExpense")
            {
                currentBalance -= adj.Amount;
                history.Add(new Transaction 
                { 
                    Amount = -adj.Amount, // Expense = Negative
                    TransactionDate = DateTimeOffset.Now, 
                    Description = "SIMULATION: " + adj.Description,
                    Category = "SIMULATION"
                });
            }
            else if (adj.Type == "OneOffIncome")
            {
                currentBalance += adj.Amount;
                history.Add(new Transaction 
                { 
                    Amount = adj.Amount, // Income = Positive
                    TransactionDate = DateTimeOffset.Now, 
                    Description = "SIMULATION INCOME: " + adj.Description,
                    Category = "CREDIT"
                });
            }
            else if (adj.Type == "MonthlyExpense")
            {
                // Add to history as if it happened every month for the last 3 months
                for (int i = 0; i < 3; i++)
                {
                    history.Add(new Transaction 
                    { 
                        Amount = -adj.Amount, // Expense = Negative
                        TransactionDate = DateTimeOffset.Now.AddMonths(-i).AddDays(-1), 
                        Description = "SIMULATION DEBIT ORDER: " + adj.Description,
                        Category = "DEBIT"
                    });
                }
            }
            else if (adj.Type == "MonthlyIncome")
            {
                for (int i = 0; i < 3; i++)
                {
                    history.Add(new Transaction 
                    { 
                        Amount = adj.Amount, // Income = Positive
                        TransactionDate = DateTimeOffset.Now.AddMonths(-i).AddDays(-1), 
                        Description = "SIMULATION SALARY: " + adj.Description,
                        Category = "CREDIT"
                    });
                }
            }
        }

        var report = await _actuarialService.AnalyzeHealthAsync(history, currentBalance, settings);
        
        return Ok(report);
    }
}

public class SimulationRequest
{
    public List<SimulationAdjustment> Adjustments { get; set; } = new();
}

public class SimulationAdjustment
{
    public string Type { get; set; } = "";
    public decimal Amount { get; set; }
    public string Description { get; set; } = "";
}
