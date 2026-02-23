using Dapper;
using GordonWorker.Models;
using Npgsql;
using System.Text.Json;

namespace GordonWorker.Services;

public interface IFinancialReportService
{
    Task GenerateAndSendReportAsync(int userId);
    Task<string> GetHealthStatsJsonAsync(int userId);
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

    private async Task<(FinancialHealthReport Report, decimal CurrentBalance, string JsonStats, AppSettings Settings)> BuildHealthReportAsync(int userId)
    {
        var settings = await _settingsService.GetSettingsAsync(userId);
        _investecClient.Configure(settings.InvestecClientId, settings.InvestecSecret, settings.InvestecApiKey, settings.InvestecBaseUrl);

        var accounts = await _investecClient.GetAccountsAsync();
        
        var balanceTasks = accounts.Select(acc => _investecClient.GetAccountBalanceAsync(acc.AccountId));
        var balances = await Task.WhenAll(balanceTasks);
        decimal currentBalance = balances.Sum();

        using var connection = new NpgsqlConnection(_configuration.GetConnectionString("DefaultConnection"));
        await connection.OpenAsync();

        var fullHistorySql = "SELECT * FROM transactions WHERE user_id = @userId AND transaction_date >= NOW() - INTERVAL '400 days'";
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
            SpendSameMonthLastYear = healthReport.SpendSameMonthLastYear.ToString("F2"),
            YoYChangePercentage = healthReport.YoYChangePercentage.ToString("F1"),
            ProjectedTotalSpendForCycle = healthReport.ProjectedMonthEndSpend.ToString("F2"),
            UpcomingExpectedPaymentsTotal = healthReport.UpcomingExpectedPayments.ToString("F2"),
            UpcomingExpectedSalaryAmount = healthReport.LastDetectedSalaryAmount.ToString("F2"),
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
                }).ToList(),
            TransactionNotes = fullHistory
                .Where(t => !string.IsNullOrWhiteSpace(t.Notes))
                .Select(t => new { t.Description, t.Amount, t.Notes })
                .ToList(),
            RecentSignificantTransactions = fullHistory
                .Where(t => Math.Abs(t.Amount) > 500)
                .OrderByDescending(t => t.TransactionDate)
                .Take(10)
                .Select(t => new { t.TransactionDate, t.Description, t.Amount, t.Category })
                .ToList()
        };

        return (healthReport, currentBalance, JsonSerializer.Serialize(stats), settings);
    }

    private DateTime ToDate(DateTimeOffset dto) => dto.LocalDateTime.Date;

    public async Task<string> GetHealthStatsJsonAsync(int userId)
    {
        var data = await BuildHealthReportAsync(userId);
        return data.JsonStats;
    }

    public async Task GenerateAndSendReportAsync(int userId)
    {
        var data = await BuildHealthReportAsync(userId);
        
        string aiExplanation;
        try 
        {
            aiExplanation = await _aiService.GenerateSimpleReportAsync(userId, data.JsonStats);
            if (string.IsNullOrWhiteSpace(aiExplanation) || aiExplanation.Contains("Error:") || aiExplanation.Contains("I'm sorry"))
            {
                aiExplanation = "<i>Note: The executive AI summary is currently unavailable. Please review the automated data metrics below.</i>";
            }
        }
        catch 
        {
            aiExplanation = "<i>Note: The executive AI summary is currently unavailable. Please review the automated data metrics below.</i>";
        }
        
        var settings = data.Settings;
        var healthReport = data.Report;
        var currentBalance = data.CurrentBalance;

        var culture = (System.Globalization.CultureInfo)System.Globalization.CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.CurrencySymbol = "R";
        culture.NumberFormat.CurrencyGroupSeparator = ",";
        culture.NumberFormat.CurrencyDecimalSeparator = ".";
        culture.NumberFormat.CurrencyDecimalDigits = 2;
        culture.NumberFormat.CurrencyPositivePattern = 0;
        culture.NumberFormat.CurrencyNegativePattern = 1;

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
                    <td>{System.Net.WebUtility.HtmlEncode(cat.Name)}</td>
                    <td style='text-align: right;' class='amount'>{cat.Amount.ToString("C", culture)}</td>
                    <td style='text-align: right; color: {changeColor}; font-size: 12px; font-weight: 600;'>{changeSign} {changeText}</td>
                </tr>";
        }

        var upcomingRows = "";
        foreach (var cost in healthReport.UpcomingFixedCosts)
        {
            upcomingRows += $@"
                <tr>
                    <td>{System.Net.WebUtility.HtmlEncode(cost.Name)}</td>
                    <td style='text-align: right;' class='amount'>{cost.ExpectedAmount.ToString("C", culture)}</td>
                    <td style='text-align: right; color: #6b7280; font-size: 12px;'>Expected</td>
                </tr>";
        }

        // Sanitize AI Output
        var safeAiExplanation = aiExplanation
            .Replace("<script", "&lt;script", StringComparison.OrdinalIgnoreCase)
            .Replace("javascript:", "javascript_:", StringComparison.OrdinalIgnoreCase)
            .Replace("onclick", "on_click", StringComparison.OrdinalIgnoreCase);

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
        .chat-section {{ background-color: #f8fafc; padding: 20px; border-radius: 8px; margin-top: 24px; border: 1px solid #e2e8f0; }}
    </style>
</head>
<body>
    <div class='wrapper'>
        <br>
        <table class='main'>
            <tr><td class='header'><h1>Gordon Finance Engine</h1></td></tr>
            <tr>
                <td class='content'>
                    <div class='ai-box' style='display: {(string.IsNullOrWhiteSpace(safeAiExplanation) ? "none" : "block")};'>
                        <h3>💡 Insights from {System.Net.WebUtility.HtmlEncode(personaName)}</h3>
                        {safeAiExplanation}
                    </div>
                    <div class='stats-header'>Financial Pulse (Salary Period)</div>
                    <table class='stats-table'>
                        <tr><th>Metric</th><th style='text-align: right;'>Value</th></tr>
                        <tr><td>Spend This Period</td><td style='text-align: right;' class='amount'>{healthReport.SpendThisMonth.ToString("C", culture)}</td></tr>
                        <tr><td>Spend Last Period</td><td style='text-align: right;' class='amount'>{healthReport.SpendLastMonth.ToString("C", culture)}</td></tr>
                        <tr style='display: {(healthReport.SpendSameMonthLastYear > 0 ? "table-row" : "none")};'>
                            <td>Vs Same Period Last Year</td>
                            <td style='text-align: right;' class='amount {(healthReport.YoYChangePercentage > 5 ? "highlight-bad" : (healthReport.YoYChangePercentage < -5 ? "highlight-good" : ""))}'>
                                {healthReport.YoYChangePercentage:F1}%
                            </td>
                        </tr>
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
        
        <!-- PROFESSIONAL SIGNATURE -->
        <table style='margin: 0 auto; width: 100%; max-width: 600px; font-family: Helvetica, Arial, sans-serif; margin-top: 20px;'>
            <tr>
                <td style='padding: 20px 0; border-top: 1px solid #e5e7eb;'>
                    <table cellpadding='0' cellspacing='0' border='0'>
                        <tr>
                            <td style='padding-right: 15px; vertical-align: top;'>
                                <div style='width: 48px; height: 48px; background-color: #0f172a; border-radius: 12px; color: #ffffff; font-size: 24px; font-weight: bold; line-height: 48px; text-align: center;'>G</div>
                            </td>
                            <td style='vertical-align: top;'>
                                <div style='font-size: 16px; font-weight: 700; color: #1e293b; margin-bottom: 4px;'>{personaName}</div>
                                <div style='font-size: 12px; color: #0ea5e9; font-weight: 600; text-transform: uppercase; letter-spacing: 0.5px; margin-bottom: 4px;'>Personal Financial Actuary</div>
                                <div style='font-size: 12px; color: #64748b;'>Powered by <strong>Gordon Finance Engine</strong></div>
                            </td>
                        </tr>
                    </table>
                    <div style='margin-top: 24px; font-size: 10px; color: #94a3b8; line-height: 1.5; text-align: justify;'>
                        <strong>CONFIDENTIALITY NOTICE:</strong> The contents of this email message and any attachments are intended solely for the addressee(s) and may contain confidential and/or privileged information and may be legally protected from disclosure. If you are not the intended recipient of this message or their agent, or if this message has been addressed to you in error, please immediately alert the sender and then destroy this message and any attachments. Generated at {DateTime.Now:yyyy-MM-dd HH:mm}.
                    </div>
                </td>
            </tr>
        </table>
    </div>
</body>
</html>";

        await _emailService.SendEmailAsync(userId, subject, body);
        
        // Telegram report disabled for email generation flow as requested
        /*
        var telegramSummary = $"📊 *Weekly Financial Report*\n\n{aiExplanation}\n\n" +
                              $"💰 *Current Balance:* {currentBalance.ToString("C", culture)}\n" +
                              $"📅 *Next Salary In:* {healthReport.DaysUntilNextSalary} Days\n" +
                              $"🚀 *Survival Prob:* {healthReport.RunwayProbability:F1}%\n" +
                              $"📈 *Trend:* {healthReport.TrendDirection}";
        
        await _telegramService.SendMessageAsync(userId, telegramSummary);
        */
    }
}
