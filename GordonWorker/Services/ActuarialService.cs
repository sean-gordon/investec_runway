using GordonWorker.Models;

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
    double RunwayProbability // Probability of lasting 30 days
);

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
        // Expense = Amount > 0 AND Category != 'CREDIT'
        var expenses = history.Where(t => t.Amount > 0 && !string.Equals(t.Category, "CREDIT", StringComparison.OrdinalIgnoreCase)).ToList();
        
        // 2. Date Context (Use Local Server Time or force SAST if needed)
        var today = DateTime.Today;
        var startOfThisMonth = new DateTime(today.Year, today.Month, 1);
        var startOfLastMonth = startOfThisMonth.AddMonths(-1);
        
        // Helper to normalize transaction date to local midnight for comparison
        DateTime ToDate(DateTimeOffset dto) => dto.LocalDateTime.Date;

        var spendThisMonth = expenses
            .Where(t => ToDate(t.TransactionDate) >= startOfThisMonth)
            .Sum(t => t.Amount);
            
        var spendLastMonth = expenses
            .Where(t => ToDate(t.TransactionDate) >= startOfLastMonth && ToDate(t.TransactionDate) < startOfThisMonth)
            .Sum(t => t.Amount);

        // 3. Linear Regression for Month-End Projection
        decimal projectedMonthEnd = spendThisMonth;
        var daysPassed = (today - startOfThisMonth).TotalDays + 1; // Include today
        var daysInMonth = DateTime.DaysInMonth(today.Year, today.Month);
        
        if (daysPassed >= 1)
        {
            projectedMonthEnd = (spendThisMonth / (decimal)daysPassed) * daysInMonth;
        }

        // 4. Daily Aggregation & Burn Rate Calculation
        var analysisWindowDays = settings.AnalysisWindowDays > 0 ? settings.AnalysisWindowDays : 90;
        var windowStartDate = today.AddDays(-analysisWindowDays);

        var validExpenses = expenses.Where(t => ToDate(t.TransactionDate) >= windowStartDate).ToList();
        var totalWindowSpend = validExpenses.Sum(t => t.Amount);
        
        // Average Daily Spend
        var avgDailySpend = (double)totalWindowSpend / analysisWindowDays;

        // Group by normalized date
        var dailyExpensesMap = validExpenses
            .GroupBy(t => ToDate(t.TransactionDate))
            .ToDictionary(g => g.Key, g => (double)g.Sum(t => t.Amount));

        // Calculate Volatility & Weighted Burn
        double sumSquaredDiff = 0;
        decimal weightedBurn = (decimal)avgDailySpend;
        decimal alpha = settings.ActuarialAlpha; 
        
        // Iterate accurately through every calendar day in the window
        for (int i = 0; i < analysisWindowDays; i++)
        {
            var loopDate = windowStartDate.AddDays(i);
            var dailySpend = dailyExpensesMap.ContainsKey(loopDate) ? dailyExpensesMap[loopDate] : 0.0;
            
            // Volatility
            sumSquaredDiff += Math.Pow(dailySpend - avgDailySpend, 2);
            
            // EMA
            weightedBurn = ((decimal)dailySpend * alpha) + (weightedBurn * (1 - alpha));
        }
        
        var stdDev = Math.Sqrt(sumSquaredDiff / analysisWindowDays);

        // 7. Runway Scenarios
        var baseBurn = weightedBurn > 0 ? weightedBurn : (decimal)avgDailySpend;
        if (baseBurn <= 0) baseBurn = 1; // Prevent div/0 if literally 0 spend

        var expectedRunway = currentBalance / baseBurn;
        var stressBurn = (decimal)((double)baseBurn + stdDev); 
        var safeRunway = stressBurn > 0 ? currentBalance / stressBurn : 0;
        var lowBurn = (decimal)Math.Max((double)baseBurn - stdDev, 1.0);
        var optimisticRunway = currentBalance / lowBurn;

        // 8. Value at Risk (VaR 95%)
        var maxProbableDailySpend = avgDailySpend + (1.645 * stdDev);

        // 9. Trend Analysis
        var trend = "Stable";
        if (analysisWindowDays >= 60)
        {
            var period1Start = windowStartDate;
            var period2Start = windowStartDate.AddDays(30);
            
            // Use ToDate() helper
            var period1Spend = expenses.Where(t => ToDate(t.TransactionDate) >= period1Start && ToDate(t.TransactionDate) < period2Start).Sum(t => t.Amount);
            var period2Spend = expenses.Where(t => ToDate(t.TransactionDate) >= period2Start && ToDate(t.TransactionDate) < period2Start.AddDays(30)).Sum(t => t.Amount);
            
            if (period2Spend > period1Spend * 1.1m) trend = "Deteriorating (Spending Accelerating)";
            else if (period2Spend < period1Spend * 0.9m) trend = "Improving (Spending Decelerating)";
        }

        // 10. Monte Carlo Probability
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
            SafeRunwayDays: safeRunway,
            ExpectedRunwayDays: expectedRunway,
            OptimisticRunwayDays: optimisticRunway,
            ValueAtRisk95: (decimal)maxProbableDailySpend,
            TrendDirection: trend,
            SpendThisMonth: spendThisMonth,
            SpendLastMonth: spendLastMonth,
            ProjectedMonthEndSpend: projectedMonthEnd,
            RunwayProbability: Math.Min(Math.Max(probSurvival, 0), 100)
        );
    }

    // Standard Normal CDF approximation
    private double CumulativeDistributionFunction(double x)
    {
        // Constants
        double a1 =  0.254829592;
        double a2 = -0.284496736;
        double a3 =  1.421413741;
        double a4 = -1.453152027;
        double a5 =  1.061405429;
        double p  =  0.3275911;

        // Save the sign of x
        int sign = 1;
        if (x < 0)
            sign = -1;
        x = Math.Abs(x) / Math.Sqrt(2.0);

        // A&S formula 7.1.26
        double t = 1.0 / (1.0 + p * x);
        double y = 1.0 - (((((a5 * t + a4) * t) + a3) * t + a2) * t + a1) * t * Math.Exp(-x * x);

        return 0.5 * (1.0 + sign * y);
    }
}