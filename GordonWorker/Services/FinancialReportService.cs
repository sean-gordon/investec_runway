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
        
        // CSS for cleaner email
        var style = @"
            <style>
                body { font-family: sans-serif; color: #333; line-height: 1.6; }
                .container { max-width: 600px; margin: 0 auto; }
                h1 { color: #003366; border-bottom: 2px solid #003366; padding-bottom: 10px; }
                .stats-table { width: 100%; border-collapse: collapse; margin: 20px 0; }
                .stats-table th, .stats-table td { text-align: left; padding: 12px; border-bottom: 1px solid #ddd; }
                .stats-table th { background-color: #f8f9fa; color: #666; font-weight: 600; }
                .highlight { color: #d32f2f; font-weight: bold; }
                .positive { color: #388e3c; font-weight: bold; }
            </style>";

        var body = $@"
            <html>
            <head>{style}</head>
            <body>
                <div class='container'>
                    <h1>Weekly Finance Update</h1>
                    
                    <div>
                        {aiExplanation}
                    </div>
                    
                    <h3>Key Statistics</h3>
                    <table class='stats-table'>
                        <tr>
                            <th>Metric</th>
                            <th>Value</th>
                        </tr>
                        <tr>
                            <td>Current Balance</td>
                            <td>{currentBalance:C} (ZAR)</td>
                        </tr>
                        <tr>
                            <td>Spend This Week</td>
                            <td>{Math.Abs(thisWeekSpend):C}</td>
                        </tr>
                        <tr>
                            <td>Spend Last Week</td>
                            <td>{Math.Abs(lastWeekSpend):C}</td>
                        </tr>
                        <tr>
                            <td>Estimated Runway</td>
                            <td class='{(healthReport.ExpectedRunwayDays < 30 ? "highlight" : "positive")}'>{healthReport.ExpectedRunwayDays:F0} Days</td>
                        </tr>
                        <tr>
                            <td>Trend</td>
                            <td>{healthReport.TrendDirection}</td>
                        </tr>
                    </table>
                    
                    <p style='font-size: 12px; color: #999; margin-top: 30px;'>
                        Generated by Gordon Finance Engine at {DateTime.Now:HH:mm}
                    </p>
                </div>
            </body>
            </html>
        ";
        
        // Ensure CultureInfo is set to South Africa for currency formatting if not system default
        var culture = System.Globalization.CultureInfo.GetCultureInfo("en-ZA");
        body = string.Format(culture, body); 

        await _emailService.SendEmailAsync(subject, body);
        _logger.LogInformation("Financial Report generated and sent.");
    }
}
