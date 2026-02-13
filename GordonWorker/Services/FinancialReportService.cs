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
        var body = $"<html><body><h1>Weekly Report for {settings.UserName}</h1><p>{aiExplanation}</p></body></html>"; // Simplified for brevity

        await _emailService.SendEmailAsync(userId, subject, body);
        
        var telegramSummary = $"📊 *Weekly Financial Report*\n\n{aiExplanation}\n\n" +
                              $"💰 *Current Balance:* {currentBalance.ToString("C", culture)}\n" +
                              $"📅 *Next Salary In:* {healthReport.DaysUntilNextSalary} Days\n" +
                              $"🚀 *Survival Prob:* {healthReport.RunwayProbability:F1}%\n" +
                              $"📈 *Trend:* {healthReport.TrendDirection}";
        
        await _telegramService.SendMessageAsync(userId, telegramSummary);
    }
}
