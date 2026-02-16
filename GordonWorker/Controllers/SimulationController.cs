using Dapper;
using GordonWorker.Models;
using GordonWorker.Services;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using System.Security.Claims;

namespace GordonWorker.Controllers;

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
        // Default to user 1 if no auth context (POC)
        int userId = 1;
        if (User?.Identity?.IsAuthenticated == true) 
        {
             int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out userId);
        }

        var settings = await _settingsService.GetSettingsAsync(userId);
        
        using var connection = new NpgsqlConnection(_configuration.GetConnectionString("DefaultConnection"));
        var history = (await connection.QueryAsync<Transaction>("SELECT * FROM transactions WHERE user_id = @userId AND transaction_date >= NOW() - INTERVAL '90 days'", new { userId })).ToList();
        
        // Use client directly or assume balance is passed? 
        // For simulation, fetching live balance might be slow. 
        // Let's rely on cached balance or fetch it.
        _investecClient.Configure(settings.InvestecClientId, settings.InvestecSecret, settings.InvestecApiKey);
        var accounts = await _investecClient.GetAccountsAsync();
        decimal currentBalance = 0;
        foreach (var acc in accounts) currentBalance += await _investecClient.GetAccountBalanceAsync(acc.AccountId);

        // Apply Adjustments
        foreach (var adj in request.Adjustments)
        {
            if (adj.Type == "OneOffExpense")
            {
                currentBalance -= adj.Amount;
                history.Add(new Transaction 
                { 
                    Amount = adj.Amount, 
                    TransactionDate = DateTimeOffset.Now, 
                    Description = "SIMULATION: " + adj.Description,
                    Category = "SIMULATION"
                });
            }
            else if (adj.Type == "OneOffIncome")
            {
                currentBalance += adj.Amount;
            }
            else if (adj.Type == "MonthlyExpense")
            {
                // Add to history as if it happened every month for the last 3 months
                for (int i = 0; i < 3; i++)
                {
                    history.Add(new Transaction 
                    { 
                        Amount = adj.Amount, 
                        TransactionDate = DateTimeOffset.Now.AddMonths(-i), 
                        Description = "SIMULATION RECURRING: " + adj.Description,
                         Category = "SIMULATION"
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
