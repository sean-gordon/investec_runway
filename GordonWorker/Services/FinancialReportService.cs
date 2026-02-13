using Dapper;
using GordonWorker.Models;
using Npgsql;
using System.Text.Json;

namespace GordonWorker.Services;

public interface IFinancialReportService
{
    Task GenerateAndSendReportAsync(int userId);
}

public class FinancialReportService : IFinancialReportService
{
    private readonly IConfiguration _configuration;
    private readonly IEmailService _emailService;
    private readonly IActuarialService _actuarialService;
    private readonly IAiService _aiService;
    private readonly ISettingsService _settingsService;
    private readonly ITelegramService _telegramService;
    private readonly IInvestecClient _investecClient;
    private readonly ILogger<FinancialReportService> _logger;

    public FinancialReportService(
        IConfiguration configuration,
        IEmailService emailService,
        IActuarialService actuarialService,
        IAiService aiService,
        ISettingsService settingsService,
        ITelegramService telegramService,
        IInvestecClient investecClient,
        ILogger<FinancialReportService> logger)
    {
        _configuration = configuration;
        _emailService = emailService;
        _actuarialService = actuarialService;
        _aiService = aiService;
        _settingsService = settingsService;
        _telegramService = telegramService;
        _investecClient = investecClient;
        _logger = logger;
    }

    public async Task GenerateAndSendReportAsync(int userId)
    {
        var settings = await _settingsService.GetSettingsAsync(userId);
        _investecClient.Configure(settings.InvestecClientId, settings.InvestecSecret, settings.InvestecApiKey, settings.InvestecBaseUrl);

        var culture = (System.Globalization.CultureInfo)System.Globalization.CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.CurrencySymbol = "R";
        culture.NumberFormat.CurrencyGroupSeparator = ",";
        culture.NumberFormat.CurrencyDecimalSeparator = ".";
        culture.NumberFormat.CurrencyDecimalDigits = 2;
        culture.NumberFormat.CurrencyPositivePattern = 0; 
        culture.NumberFormat.CurrencyNegativePattern = 1; 

        var accounts = await _investecClient.GetAccountsAsync();
        decimal currentBalance = 0;
        foreach (var acc in accounts)
        {
            currentBalance += await _investecClient.GetAccountBalanceAsync(acc.AccountId);
        }

        using var connection = new NpgsqlConnection(_configuration.GetConnectionString("DefaultConnection"));
        await connection.OpenAsync();

        var fullHistorySql = "SELECT * FROM transactions WHERE user_id = @userId AND transaction_date >= NOW() - INTERVAL '90 days'";
        var fullHistory = (await connection.QueryAsync<Transaction>(fullHistorySql, new { userId })).ToList();
        
        var healthReport = await _actuarialService.AnalyzeHealthAsync(fullHistory, currentBalance, settings);

        var stats = new
        {
            UserName = settings.UserName,
            CurrentBalance = currentBalance.ToString("F2"),
            DaysUntilNextSalary = healthReport.DaysUntilNextSalary,
            ProjectedBalanceAtPayday = healthReport.ProjectedBalanceAtNextSalary.ToString("F2"),
            SpendThisPeriod = healthReport.SpendThisMonth.ToString("F2"),
            SpendLastPeriod = healthReport.SpendLastMonth.ToString("F2"),
            ProjectedTotalSpendForCycle = healthReport.ProjectedMonthEndSpend.ToString("F2"),
            UpcomingExpectedPaymentsTotal = healthReport.UpcomingExpectedPayments.ToString("F2"),
            RunwayDays = healthReport.ExpectedRunwayDays.ToString("F1"),
            ProbabilityToReachPayday = healthReport.RunwayProbability.ToString("F1") + "%",
            CurrencySymbol = "R",
            AllTopCategoriesAreStable = healthReport.TopCategories.All(c => c.IsStable),
            UpcomingFixedCosts = healthReport.UpcomingFixedCosts.Select(e => new { e.Name, Amount = e.ExpectedAmount.ToString("F2") }).ToList(),
            TopCategoriesWithIncreases = healthReport.TopCategories
                .Where(c => !c.IsStable) 
                .Select(c => new { 
                    CategoryName = c.Name, 
                    TotalAmountSpentThisPeriod = c.Amount.ToString("F2"), 
                    IncreasePercentFromLastPeriod = c.ChangePercentage,
                    IsFixedUncontrollableCost = c.IsFixedCost
                }).ToList()
        };

        var jsonStats = JsonSerializer.Serialize(stats);
        var aiExplanation = await _aiService.GenerateSimpleReportAsync(userId, jsonStats);

        var subject = $"Weekly Financial Report - {DateTime.Now:dd MMM yyyy}";
        var personaName = !string.IsNullOrWhiteSpace(settings.SystemPersona) ? settings.SystemPersona : "Gordon";

        // HTML Email Template
        var categoryRows = "";
        foreach (var cat in healthReport.TopCategories)
        {
            bool isGrowing = !cat.IsStable && cat.ChangePercentage > 0.01m;
            bool isShrinking = !cat.IsStable && cat.ChangePercentage < -0.01m;
            var changeColor = isGrowing ? "#dc2626" : (isShrinking ? "#059669" : "#6b7280");
            var changeSign = isGrowing ? "▲" : (isShrinking ? "▼" : "•");
            var changeText = cat.IsStable ? "Stable" : $"{Math.Abs(cat.ChangePercentage):F0}%";

            categoryRows += $@"
                <tr>
                    <td>{cat.Name}</td>
                    <td style='text-align: right;' class='amount'>{cat.Amount.ToString("C", culture)}</td>
                    <td style='text-align: right; color: {changeColor}; font-size: 12px; font-weight: 600;'>{changeSign} {changeText}</td>
                </tr>";
        }

        var upcomingRows = "";
        foreach (var cost in healthReport.UpcomingFixedCosts)
        {
            upcomingRows += $@"
                <tr>
                    <td>{cost.Name}</td>
                    <td style='text-align: right;' class='amount'>{cost.ExpectedAmount.ToString("C", culture)}</td>
                    <td style='text-align: right; color: #6b7280; font-size: 12px;'>Expected</td>
                </tr>";
        }

        var body = $@"
<!DOCTYPE html>
<html>
<head>
    <meta charset='utf-8'>
    <meta name='viewport' content='width=device-width, initial-scale=1.0'>
    <style>
        body {{ font-family: 'Helvetica Neue', Helvetica, Arial, sans-serif; background-color: #f3f4f6; margin: 0; padding: 0; }}
        .wrapper {{ width: 100%; table-layout: fixed; background-color: #f3f4f6; padding-bottom: 40px; }}
        .main {{ background-color: #ffffff; margin: 0 auto; width: 100%; max-width: 600px; border-spacing: 0; color: #171717; border-radius: 8px; overflow: hidden; box-shadow: 0 4px 6px -1px rgba(0, 0, 0, 0.1); }}
        .header {{ background-color: #111827; padding: 24px; text-align: center; }}
        .header h1 {{ color: #ffffff; margin: 0; font-size: 24px; font-weight: 600; letter-spacing: -0.5px; }}
        .content {{ padding: 32px 24px; }}
        .ai-box {{ background-color: #f0f9ff; border-left: 4px solid #0ea5e9; padding: 20px; border-radius: 4px; margin-bottom: 24px; font-size: 16px; line-height: 1.6; color: #334155; }}
        .ai-box h3 {{ margin-top: 0; color: #0369a1; font-size: 16px; text-transform: uppercase; letter-spacing: 0.5px; font-weight: 700; margin-bottom: 12px; }}
        .ai-box ul {{ margin: 0; padding-left: 24px; list-style-type: disc; }}
        .ai-box li {{ margin-bottom: 8px; }}
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
            <tr><td class='header'><h1>Gordon Finance Engine</h1></td></tr>
            <tr>
                <td class='content'>
                    <div class='ai-box' style='display: {(string.IsNullOrWhiteSpace(aiExplanation) ? "none" : "block")};'>
                        <h3>💡 Insights from {personaName}</h3>
                        {aiExplanation}
                    </div>
                    <div class='stats-header'>Financial Pulse (Salary Period)</div>
                    <table class='stats-table'>
                        <tr><th>Metric</th><th style='text-align: right;'>Value</th></tr>
                        <tr><td>Spend This Period</td><td style='text-align: right;' class='amount'>{healthReport.SpendThisMonth.ToString("C", culture)}</td></tr>
                        <tr><td>Spend Last Period</td><td style='text-align: right;' class='amount'>{healthReport.SpendLastMonth.ToString("C", culture)}</td></tr>
                        <tr><td>Projected Cycle Spend</td><td style='text-align: right;' class='amount highlight-warn'>{healthReport.ProjectedMonthEndSpend.ToString("C", culture)}</td></tr>
                        <tr><td>Projected Payday Balance</td><td style='text-align: right;' class='amount highlight-good'>{healthReport.ProjectedBalanceAtNextSalary.ToString("C", culture)}</td></tr>
                    </table>
                    
                    <div style='display: {(healthReport.UpcomingFixedCosts.Any() ? "block" : "none")};'>
                        <div class='stats-header'>Upcoming Expected Payments</div>
                        <table class='stats-table'>
                            <tr><th>Description</th><th style='text-align: right;'>Avg Amount</th><th style='text-align: right;'>Status</th></tr>
                            {upcomingRows}
                            <tr style='background-color: #f9fafb; font-weight: 700;'>
                                <td>TOTAL UPCOMING</td>
                                <td style='text-align: right;' class='amount'>{healthReport.UpcomingExpectedPayments.ToString("C", culture)}</td>
                                <td></td>
                            </tr>
                        </table>
                    </div>

                    <div class='stats-header'>Top Spending Categories</div>
                    <table class='stats-table'>
                        <tr><th>Category</th><th style='text-align: right;'>Amount</th><th style='text-align: right;'>Vs Last Period</th></tr>
                        {categoryRows}
                    </table>
                    <div class='stats-header'>Runway & Risk</div>
                    <table class='stats-table'>
                        <tr><th>Metric</th><th style='text-align: right;'>Value</th></tr>
                        <tr><td>Current Balance</td><td style='text-align: right;' class='amount'>{currentBalance.ToString("C", culture)}</td></tr>
                        <tr><td>Next Salary In</td><td style='text-align: right;'>{healthReport.DaysUntilNextSalary} Days</td></tr>
                        <tr><td>Projected Runway</td><td style='text-align: right;' class='{(healthReport.ExpectedRunwayDays < 30 ? "highlight-bad" : "highlight-good")}'>{healthReport.ExpectedRunwayDays:F0} Days</td></tr>
                        <tr><td>Survival Probability (To Payday)</td><td style='text-align: right;' class='{(healthReport.RunwayProbability < 80 ? "highlight-bad" : "highlight-good")}'>{healthReport.RunwayProbability:F1}%</td></tr>
                        <tr><td>Spending Trend</td><td style='text-align: right;'>{healthReport.TrendDirection}</td></tr>
                    </table>
                </td>
            </tr>
        </table>
        <div class='footer'>Generated automatically by your Gordon Finance Engine.<br>{DateTime.Now:yyyy-MM-dd HH:mm}</div>
    </div>
</body>
</html>";

        await _emailService.SendEmailAsync(userId, subject, body);
        
        var telegramSummary = $"📊 *Weekly Financial Report*\n\n{aiExplanation}\n\n" +
                              $"💰 *Current Balance:* {currentBalance.ToString("C", culture)}\n" +
                              $"📅 *Next Salary In:* {healthReport.DaysUntilNextSalary} Days\n" +
                              $"🚀 *Survival Prob:* {healthReport.RunwayProbability:F1}%\n" +
                              $"📈 *Trend:* {healthReport.TrendDirection}";
        
        await _telegramService.SendMessageAsync(userId, telegramSummary);
    }
}
