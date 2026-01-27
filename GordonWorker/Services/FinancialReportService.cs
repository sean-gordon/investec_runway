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
    private readonly ISettingsService _settingsService;
    private readonly IInvestecClient _investecClient;
    private readonly ILogger<FinancialReportService> _logger;

    public FinancialReportService(
        IConfiguration configuration,
        IEmailService emailService,
        IActuarialService actuarialService,
        IOllamaService ollamaService,
        ISettingsService settingsService,
        IInvestecClient investecClient,
        ILogger<FinancialReportService> logger)
    {
        _configuration = configuration;
        _emailService = emailService;
        _actuarialService = actuarialService;
        _ollamaService = ollamaService;
        _settingsService = settingsService;
        _investecClient = investecClient;
        _logger = logger;
    }

    public async Task GenerateAndSendReportAsync()
    {
        var settings = await _settingsService.GetSettingsAsync();
        
        // 0. Fetch Real-time Balance
        var accounts = await _investecClient.GetAccountsAsync();
        decimal currentBalance = 0;
        foreach (var acc in accounts)
        {
            currentBalance += await _investecClient.GetAccountBalanceAsync(acc.AccountId);
        }

        using var connection = new NpgsqlConnection(_configuration.GetConnectionString("DefaultConnection"));
        await connection.OpenAsync();

        // 1. Get Data (Last 14 days for comparison)
        var sql = "SELECT * FROM transactions WHERE transaction_date >= NOW() - INTERVAL '14 days'";
        var transactions = (await connection.QueryAsync<Transaction>(sql)).ToList();

        var thisWeek = transactions.Where(t => t.TransactionDate >= DateTimeOffset.UtcNow.AddDays(-7)).ToList();
        var lastWeek = transactions.Where(t => t.TransactionDate < DateTimeOffset.UtcNow.AddDays(-7)).ToList();

        // 3. Actuarial Analysis
        var fullHistorySql = "SELECT * FROM transactions WHERE transaction_date >= NOW() - INTERVAL '90 days'";
        var fullHistory = (await connection.QueryAsync<Transaction>(fullHistorySql)).ToList();
        var healthReport = _actuarialService.AnalyzeHealth(fullHistory, currentBalance);

        // 4. Prepare Comparison Stats
        // Expense = Amount > 0 AND Category != 'CREDIT'
        var thisWeekSpend = thisWeek
            .Where(t => t.Amount > 0 && !string.Equals(t.Category, "CREDIT", StringComparison.OrdinalIgnoreCase))
            .Sum(t => t.Amount);
            
        var lastWeekSpend = lastWeek
            .Where(t => t.Amount > 0 && !string.Equals(t.Category, "CREDIT", StringComparison.OrdinalIgnoreCase))
            .Sum(t => t.Amount);

        var stats = new
        {
            UserName = settings.UserName,
            CurrentBalance = currentBalance,
            SpendThisWeek = Math.Abs(thisWeekSpend),
            SpendLastWeek = Math.Abs(lastWeekSpend),
            RunwayDays = healthReport.ExpectedRunwayDays,
            Volatility = healthReport.BurnVolatility,
            Trend = healthReport.TrendDirection,
            Currency = settings.CurrencyCulture,
            SpendThisMonth = healthReport.SpendThisMonth,
            SpendLastMonth = healthReport.SpendLastMonth,
            ProjectedMonthEnd = healthReport.ProjectedMonthEndSpend,
            Probability30DaySurvival = healthReport.RunwayProbability
        };

        var jsonStats = JsonSerializer.Serialize(stats);

        // 5. Generate Content with AI
        var aiExplanation = await _ollamaService.GenerateSimpleReportAsync(jsonStats);

        // 6. Send Email
        var subject = $"Weekly Financial Report - {DateTime.Now:dd MMM yyyy}";
        
        // Ensure CultureInfo is set from settings
        var culture = System.Globalization.CultureInfo.GetCultureInfo(settings.CurrencyCulture);

        var body = $@"
<!DOCTYPE html>
<html>
<head>
    <meta charset='utf-8'>
    <meta name='viewport' content='width=device-width, initial-scale=1.0'>
    <style>
        body {{ font-family: 'Helvetica Neue', Helvetica, Arial, sans-serif; background-color: #f3f4f6; margin: 0; padding: 0; }}
        .wrapper {{ width: 100%; table-layout: fixed; background-color: #f3f4f6; padding-bottom: 40px; }}
        .main {{ background-color: #ffffff; margin: 0 auto; width: 100%; max-width: 600px; border-spacing: 0; font-family: sans-serif; color: #171717; border-radius: 8px; overflow: hidden; box-shadow: 0 4px 6px -1px rgba(0, 0, 0, 0.1); }}
        .header {{ background-color: #111827; padding: 24px; text-align: center; }}
        .header h1 {{ color: #ffffff; margin: 0; font-size: 24px; font-weight: 600; letter-spacing: -0.5px; }}
        .content {{ padding: 32px 24px; }}
        .ai-box {{ background-color: #f0f9ff; border-left: 4px solid #0ea5e9; padding: 20px; border-radius: 4px; margin-bottom: 24px; font-size: 16px; line-height: 1.6; color: #334155; }}
        .ai-box h3 {{ margin-top: 0; color: #0369a1; font-size: 16px; text-transform: uppercase; letter-spacing: 0.5px; font-weight: 700; margin-bottom: 12px; }}
        .ai-box ul {{ margin: 0; padding-left: 24px; list-style-type: disc; }}
        .ai-box li {{ margin-bottom: 8px; padding-left: 4px; }}
        .ai-box p {{ margin-top: 0; margin-bottom: 16px; }}
        .stats-header {{ font-size: 18px; font-weight: 700; color: #111827; margin-bottom: 16px; border-bottom: 2px solid #e5e7eb; padding-bottom: 8px; margin-top: 32px; }}
        .stats-table {{ width: 100%; border-collapse: collapse; }}
        .stats-table th {{ text-align: left; padding: 12px 8px; color: #6b7280; font-size: 12px; text-transform: uppercase; letter-spacing: 0.5px; font-weight: 600; border-bottom: 1px solid #e5e7eb; }}
        .stats-table td {{ padding: 16px 8px; border-bottom: 1px solid #f3f4f6; font-size: 15px; font-weight: 500; color: #1f2937; }}
        .stats-table tr:last-child td {{ border-bottom: none; }}
        .amount {{ font-family: monospace; font-size: 16px; color: #111827; }}
        .highlight-good {{ color: #059669; font-weight: 700; }}
        .highlight-bad {{ color: #dc2626; font-weight: 700; }}
        .highlight-warn {{ color: #d97706; font-weight: 700; }}
        .footer {{ text-align: center; padding: 24px; color: #9ca3af; font-size: 12px; }}
    </style>
</head>
<body>
    <div class='wrapper'>
        <br>
        <table class='main'>
            <!-- Header -->
            <tr>
                <td class='header'>
                    <h1>Gordon Finance Engine</h1>
                </td>
            </tr>

            <!-- Content -->
            <tr>
                <td class='content'>
                    
                    <!-- AI Insight -->
                    <div class='ai-box'>
                        <h3>💡 The Weekly Brief</h3>
                        {aiExplanation}
                    </div>

                    <!-- Monthly Pulse -->
                    <div class='stats-header'>Monthly Pulse (MTD)</div>
                    <table class='stats-table'>
                        <tr>
                            <th>Metric</th>
                            <th style='text-align: right;'>Value</th>
                        </tr>
                        <tr>
                            <td>Spend This Month</td>
                            <td style='text-align: right;' class='amount'>{string.Format(culture, "{0:C}", healthReport.SpendThisMonth)}</td>
                        </tr>
                        <tr>
                            <td>Spend Last Month</td>
                            <td style='text-align: right;' class='amount'>{string.Format(culture, "{0:C}", healthReport.SpendLastMonth)}</td>
                        </tr>
                        <tr>
                            <td>Projected Month End</td>
                            <td style='text-align: right;' class='amount highlight-warn'>{string.Format(culture, "{0:C}", healthReport.ProjectedMonthEndSpend)}</td>
                        </tr>
                    </table>

                    <!-- Vital Signs -->
                    <div class='stats-header'>Runway & Risk</div>
                    <table class='stats-table'>
                        <tr>
                            <th>Metric</th>
                            <th style='text-align: right;'>Value</th>
                        </tr>
                        <tr>
                            <td>Current Balance</td>
                            <td style='text-align: right;' class='amount'>{string.Format(culture, "{0:C}", currentBalance)}</td>
                        </tr>
                        <tr>
                            <td>Projected Runway</td>
                            <td style='text-align: right;' class='{(healthReport.ExpectedRunwayDays < 30 ? "highlight-bad" : "highlight-good")}'>
                                {healthReport.ExpectedRunwayDays:F0} Days
                            </td>
                        </tr>
                        <tr>
                            <td>30-Day Survival Probability</td>
                            <td style='text-align: right;' class='{(healthReport.RunwayProbability < 80 ? "highlight-bad" : "highlight-good")}'>
                                {healthReport.RunwayProbability:F1}%
                            </td>
                        </tr>
                        <tr>
                            <td>Spending Trend</td>
                            <td style='text-align: right;'>{healthReport.TrendDirection}</td>
                        </tr>
                    </table>

                </td>
            </tr>
        </table>
        
        <div class='footer'>
            Generated automatically by your Gordon Finance Engine.<br>
            {DateTime.Now:yyyy-MM-dd HH:mm}
        </div>
    </div>
</body>
</html>";

        await _emailService.SendEmailAsync(subject, body);
        _logger.LogInformation("Financial Report generated and sent.");
    }
}