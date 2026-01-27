using GordonWorker.Models;
using System.Text.RegularExpressions;

namespace GordonWorker.Services;

public record FinancialHealthReport(
    decimal CurrentBalance,
    decimal WeightedDailyBurn,
    decimal MonthlyBurnRate,
    double BurnVolatility,
    decimal SafeRunwayDays, // Conservative (95% confidence)
    decimal ExpectedRunwayDays, // Average
    decimal OptimisticRunwayDays, // Low spend scenario
    decimal ValueAtRisk95, // Max probable daily spend
    string TrendDirection, // "Stable", "Deteriorating", "Improving"
    decimal SpendThisMonth,
    decimal SpendLastMonth,
    decimal ProjectedMonthEndSpend,
    double RunwayProbability, // Probability of lasting 30 days
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

    public FinancialHealthReport AnalyzeHealth(List<Transaction> history, decimal currentBalance)
    {
        var settings = _settingsService.GetSettingsAsync().GetAwaiter().GetResult();

        // 1. Basic Filtering (Expenses only)
        var expenses = history.Where(t => t.Amount > 0 && !string.Equals(t.Category, "CREDIT", StringComparison.OrdinalIgnoreCase)).ToList();
        
        // 2. Date Context
        var today = DateTime.Today;
        var startOfThisMonth = new DateTime(today.Year, today.Month, 1);
        var startOfLastMonth = startOfThisMonth.AddMonths(-1);
        
        DateTime ToDate(DateTimeOffset dto) => dto.LocalDateTime.Date;

        var thisMonthExpenses = expenses.Where(t => ToDate(t.TransactionDate) >= startOfThisMonth).ToList();
        var lastMonthExpenses = expenses.Where(t => ToDate(t.TransactionDate) >= startOfLastMonth && ToDate(t.TransactionDate) < startOfThisMonth).ToList();

        var spendThisMonth = thisMonthExpenses.Sum(t => t.Amount);
        var spendLastMonth = lastMonthExpenses.Sum(t => t.Amount);

        // 3. Category Analysis (Top 3)
        // Normalize descriptions to catch main vendors (e.g. "CHECKERS 123" -> "CHECKERS")
        string Normalize(string desc) 
        {
            if (string.IsNullOrEmpty(desc)) return "Uncategorized";
            // Remove common noise like dates, numbers, and "YOCO *"
            var clean = Regex.Replace(desc, @"\d|ZA|CPT|JHB|GP|GAUTENG|PTY|LTD|\*", "").Trim();
            var parts = clean.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // Take first 2 words for context
            return parts.Length > 0 ? (parts.Length > 1 ? $"{parts[0]} {parts[1]}" : parts[0]) : "Uncategorized";
        }

        var topCategories = thisMonthExpenses
            .GroupBy(t => Normalize(t.Description))
            .Select(g => new 
            { 
                Name = g.Key, 
                Amount = g.Sum(t => t.Amount)
            })
            .OrderByDescending(x => x.Amount)
            .Take(3)
            .ToList();

        var categoryReport = new List<CategorySpend>();
        foreach (var cat in topCategories)
        {
            var lastMonthCatSpend = lastMonthExpenses
                .Where(t => Normalize(t.Description) == cat.Name)
                .Sum(t => t.Amount);
            
            decimal diff = cat.Amount - lastMonthCatSpend;
            decimal percent = lastMonthCatSpend == 0 ? 100 : (diff / lastMonthCatSpend) * 100;
            categoryReport.Add(new CategorySpend(cat.Name, cat.Amount, diff, percent));
        }

        // 4. Linear Regression for Month-End Projection
        decimal projectedMonthEnd = spendThisMonth;
        var daysPassed = (today - startOfThisMonth).TotalDays + 1; 
        var daysInMonth = DateTime.DaysInMonth(today.Year, today.Month);
        if (daysPassed >= 1) projectedMonthEnd = (spendThisMonth / (decimal)daysPassed) * daysInMonth;

        // 5. Daily Aggregation & Burn Rate
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

        // 6. Runway Scenarios
        var baseBurn = weightedBurn > 0 ? weightedBurn : (decimal)avgDailySpend;
        if (baseBurn <= 0) baseBurn = 1; 
        var expectedRunway = currentBalance / baseBurn;

        // 7. Trend Analysis
        var trend = "Stable";
        if (analysisWindowDays >= 60)
        {
            var period1Start = windowStartDate;
            var period2Start = windowStartDate.AddDays(30);
            var period1Spend = expenses.Where(t => ToDate(t.TransactionDate) >= period1Start && ToDate(t.TransactionDate) < period2Start).Sum(t => t.Amount);
            var period2Spend = expenses.Where(t => ToDate(t.TransactionDate) >= period2Start && ToDate(t.TransactionDate) < period2Start.AddDays(30)).Sum(t => t.Amount);
            if (period2Spend > period1Spend * 1.1m) trend = "Deteriorating";
            else if (period2Spend < period1Spend * 0.9m) trend = "Improving";
        }

        // 8. Monte Carlo Probability
        double probSurvival = 0;
        if (stdDev > 0)
        {
            var mean30 = (double)baseBurn * 30;
            var stdDev30 = stdDev * Math.Sqrt(30);
            var zScore = ((double)currentBalance - mean30) / stdDev30;
            probSurvival = CumulativeDistributionFunction(zScore) * 100;
        }
        else
        {
            probSurvival = currentBalance > (baseBurn * 30) ? 100 : 0;
        }

        return new FinancialHealthReport(
            CurrentBalance: currentBalance,
            WeightedDailyBurn: weightedBurn,
            MonthlyBurnRate: weightedBurn * 30,
            BurnVolatility: stdDev,
            SafeRunwayDays: currentBalance / (decimal)((double)baseBurn + stdDev),
            ExpectedRunwayDays: expectedRunway,
            OptimisticRunwayDays: currentBalance / (decimal)Math.Max((double)baseBurn - stdDev, 1.0),
            ValueAtRisk95: (decimal)(avgDailySpend + (1.645 * stdDev)),
            TrendDirection: trend,
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