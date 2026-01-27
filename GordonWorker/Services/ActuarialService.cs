using GordonWorker.Models;
using System.Text.RegularExpressions;

namespace GordonWorker.Services;

public record FinancialHealthReport(
    decimal CurrentBalance,
    decimal WeightedDailyBurn,
    decimal MonthlyBurnRate,
    double BurnVolatility,
    decimal SafeRunwayDays,
    decimal ExpectedRunwayDays,
    decimal OptimisticRunwayDays,
    decimal ValueAtRisk95,
    string TrendDirection,
    decimal SpendThisMonth,
    decimal SpendLastMonth,
    decimal ProjectedMonthEndSpend,
    double RunwayProbability,
    List<CategorySpend> TopCategories
);

public record CategorySpend(string Name, decimal Amount, decimal ChangeAmount, decimal ChangePercentage);

public interface IActuarialService
{
    FinancialHealthReport AnalyzeHealth(List<Transaction> history, decimal currentBalance);
}

public class ActuarialService : IActuarialService
{
    private readonly ISettingsService _settingsService;

    public ActuarialService(ISettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    private string NormalizeDescription(string? desc)
    {
        if (string.IsNullOrWhiteSpace(desc)) return "Uncategorized";
        // Remove numbers, special chars, and common noisy banking words
        var clean = Regex.Replace(desc, @"\d|ZA|CPT|JHB|GP|GAUTENG|PTY|LTD|\*|'|&apos;", "").Trim();
        var parts = clean.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // Return first 2 words for broad grouping
        return parts.Length > 0 ? (parts.Length > 1 ? $"{parts[0]} {parts[1]}" : parts[0]).ToUpper() : "Uncategorized";
    }

    public FinancialHealthReport AnalyzeHealth(List<Transaction> history, decimal currentBalance)
    {
        var settings = _settingsService.GetSettingsAsync().GetAwaiter().GetResult();

        var expenses = history.Where(t => t.Amount > 0 && !string.Equals(t.Category, "CREDIT", StringComparison.OrdinalIgnoreCase)).ToList();
        
        var today = DateTime.Today;
        var startOfThisMonth = new DateTime(today.Year, today.Month, 1);
        var startOfLastMonth = startOfThisMonth.AddMonths(-1);
        
        DateTime ToDate(DateTimeOffset dto) => dto.LocalDateTime.Date;

        var thisMonthExpenses = expenses.Where(t => ToDate(t.TransactionDate) >= startOfThisMonth).ToList();
        var lastMonthExpenses = expenses.Where(t => ToDate(t.TransactionDate) >= startOfLastMonth && ToDate(t.TransactionDate) < startOfThisMonth).ToList();

        var spendThisMonth = thisMonthExpenses.Sum(t => t.Amount);
        var spendLastMonth = lastMonthExpenses.Sum(t => t.Amount);

        // Category Analysis
        var topCategories = thisMonthExpenses
            .GroupBy(t => NormalizeDescription(t.Description))
            .Select(g => new { Name = g.Key, Amount = g.Sum(t => t.Amount) })
            .OrderByDescending(x => x.Amount)
            .Take(3)
            .ToList();

        var categoryReport = new List<CategorySpend>();
        foreach (var cat in topCategories)
        {
            var lastMonthCatSpend = lastMonthExpenses
                .Where(t => NormalizeDescription(t.Description) == cat.Name)
                .Sum(t => t.Amount);
            
            decimal diff = cat.Amount - lastMonthCatSpend;
            decimal percent = lastMonthCatSpend == 0 ? 100 : (diff / lastMonthCatSpend) * 100;
            categoryReport.Add(new CategorySpend(cat.Name, cat.Amount, diff, percent));
        }

        // Stats
        decimal projectedMonthEnd = spendThisMonth;
        var daysPassed = (today - startOfThisMonth).TotalDays + 1;
        var daysInMonth = DateTime.DaysInMonth(today.Year, today.Month);
        if (daysPassed >= 1) projectedMonthEnd = (spendThisMonth / (decimal)daysPassed) * daysInMonth;

        var analysisWindowDays = settings.AnalysisWindowDays > 0 ? settings.AnalysisWindowDays : 90;
        var windowStartDate = today.AddDays(-analysisWindowDays);
        var validExpenses = expenses.Where(t => ToDate(t.TransactionDate) >= windowStartDate).ToList();
        var totalWindowSpend = validExpenses.Sum(t => t.Amount);
        var avgDailySpend = (double)totalWindowSpend / analysisWindowDays;

        var dailyExpensesMap = validExpenses
            .GroupBy(t => ToDate(t.TransactionDate))
            .ToDictionary(g => g.Key, g => (double)g.Sum(t => t.Amount));

        double sumSquaredDiff = 0;
        decimal weightedBurn = (decimal)avgDailySpend;
        decimal alpha = settings.ActuarialAlpha; 
        for (int i = 0; i < analysisWindowDays; i++)
        {
            var loopDate = windowStartDate.AddDays(i);
            var dailySpend = dailyExpensesMap.ContainsKey(loopDate) ? dailyExpensesMap[loopDate] : 0.0;
            sumSquaredDiff += Math.Pow(dailySpend - avgDailySpend, 2);
            weightedBurn = ((decimal)dailySpend * alpha) + (weightedBurn * (1 - alpha));
        }
        var stdDev = Math.Sqrt(sumSquaredDiff / analysisWindowDays);

        var baseBurn = weightedBurn > 0 ? weightedBurn : (decimal)avgDailySpend;
        if (baseBurn <= 0) baseBurn = 1; 

        double probSurvival = 0;
        if (stdDev > 0)
        {
            var mean30 = (double)baseBurn * 30;
            var stdDev30 = stdDev * Math.Sqrt(30);
            var zScore = ((double)currentBalance - mean30) / stdDev30;
            probSurvival = CumulativeDistributionFunction(zScore) * 100;
        }
        else { probSurvival = currentBalance > (baseBurn * 30) ? 100 : 0; }

        return new FinancialHealthReport(
            CurrentBalance: currentBalance,
            WeightedDailyBurn: weightedBurn,
            MonthlyBurnRate: weightedBurn * 30,
            BurnVolatility: stdDev,
            SafeRunwayDays: currentBalance / (decimal)((double)baseBurn + stdDev),
            ExpectedRunwayDays: currentBalance / baseBurn,
            OptimisticRunwayDays: currentBalance / (decimal)Math.Max((double)baseBurn - stdDev, 1.0),
            ValueAtRisk95: (decimal)(avgDailySpend + (1.645 * stdDev)),
            TrendDirection: "Stable",
            SpendThisMonth: spendThisMonth,
            SpendLastMonth: spendLastMonth,
            ProjectedMonthEndSpend: projectedMonthEnd,
            RunwayProbability: Math.Min(Math.Max(probSurvival, 0), 100),
            TopCategories: categoryReport
        );
    }

    private double CumulativeDistributionFunction(double x)
    {
        double a1 = 0.254829592; double a2 = -0.284496736; double a3 = 1.421413741; double a4 = -1.453152027; double a5 = 1.061405429; double p = 0.3275911;
        int sign = 1; if (x < 0) sign = -1;
        x = Math.Abs(x) / Math.Sqrt(2.0);
        double t = 1.0 / (1.0 + p * x);
        double y = 1.0 - (((((a5 * t + a4) * t) + a3) * t + a2) * t + a1) * t * Math.Exp(-x * x);
        return 0.5 * (1.0 + sign * y);
    }
}
