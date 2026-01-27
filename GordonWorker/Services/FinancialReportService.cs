using Dapper;
using GordonWorker.Models;
using Npgsql;
using System.Text.Json;

namespace GordonWorker.Services;

public interface IFinancialReportService
{
    Task GenerateAndSendReportAsync();
}

public class FinancialReportService : IFinancialReportService
{
    private readonly IConfiguration _configuration;
    private readonly IEmailService _emailService;
    private readonly IActuarialService _actuarialService;
    private readonly IOllamaService _ollamaService;
    private readonly ILogger<FinancialReportService> _logger;

    public FinancialReportService(
        IConfiguration configuration,
        IEmailService emailService,
        IActuarialService actuarialService,
        IOllamaService ollamaService,
        ILogger<FinancialReportService> logger)
    {
        _configuration = configuration;
        _emailService = emailService;
        _actuarialService = actuarialService;
        _ollamaService = ollamaService;
        _logger = logger;
    }

    public async Task GenerateAndSendReportAsync()
    {
        using var connection = new NpgsqlConnection(_configuration.GetConnectionString("DefaultConnection"));
        await connection.OpenAsync();

        // 1. Get Data (Last 14 days for comparison)
        var sql = "SELECT * FROM transactions WHERE transaction_date >= NOW() - INTERVAL '14 days'";
        var transactions = (await connection.QueryAsync<Transaction>(sql)).ToList();

        var thisWeek = transactions.Where(t => t.TransactionDate >= DateTimeOffset.UtcNow.AddDays(-7)).ToList();
        var lastWeek = transactions.Where(t => t.TransactionDate < DateTimeOffset.UtcNow.AddDays(-7)).ToList();

        // 2. Get Current Balance
        var sqlBalance = "SELECT balance FROM transactions ORDER BY transaction_date DESC LIMIT 1";
        var currentBalance = await connection.ExecuteScalarAsync<decimal?>(sqlBalance) ?? 0;

        // 3. Actuarial Analysis
        var fullHistorySql = "SELECT * FROM transactions WHERE transaction_date >= NOW() - INTERVAL '90 days'";
        var fullHistory = (await connection.QueryAsync<Transaction>(fullHistorySql)).ToList();
        var healthReport = _actuarialService.AnalyzeHealth(fullHistory, currentBalance);

        // 4. Prepare Comparison Stats
        var thisWeekSpend = thisWeek.Where(t => t.Amount < 0).Sum(t => t.Amount);
        var lastWeekSpend = lastWeek.Where(t => t.Amount < 0).Sum(t => t.Amount);

        var stats = new
        {
            CurrentBalance = currentBalance,
            SpendThisWeek = Math.Abs(thisWeekSpend),
            SpendLastWeek = Math.Abs(lastWeekSpend),
            RunwayDays = healthReport.ExpectedRunwayDays,
            Volatility = healthReport.BurnVolatility,
            Trend = healthReport.TrendDirection
        };

        var jsonStats = JsonSerializer.Serialize(stats);

        // 5. Generate Content with AI
        var aiExplanation = await _ollamaService.GenerateSimpleReportAsync(jsonStats);

        // 6. Send Email
        var subject = $"Weekly Financial Report - {DateTime.Now:dd MMM yyyy}";
        var body = $@"
            <h1>Weekly Finance Update</h1>
            {aiExplanation}
            <hr>
            <h3>Raw Stats</h3>
            <ul>
                <li>Current Balance: {currentBalance:C}</li>
                <li>Spend This Week: {Math.Abs(thisWeekSpend):C}</li>
                <li>Spend Last Week: {Math.Abs(lastWeekSpend):C}</li>
                <li>Estimated Runway: {healthReport.ExpectedRunwayDays:F0} days</li>
            </ul>
        ";

        await _emailService.SendEmailAsync(subject, body);
        _logger.LogInformation("Financial Report generated and sent.");
    }
}
