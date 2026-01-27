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
        var expenses = history.Where(t => t.Amount > 0 && !string.Equals(t.Category, "CREDIT", StringComparison.OrdinalIgnoreCase)).ToList();
        
        // 2. Monthly Stats - Use Local Time context for "Month" boundaries to match user expectation
        var now = DateTime.Now;
        var startOfThisMonth = new DateTime(now.Year, now.Month, 1);
        var startOfLastMonth = startOfThisMonth.AddMonths(-1);
        
        var spendThisMonth = expenses
            .Where(t => t.TransactionDate.Date >= startOfThisMonth)
            .Sum(t => t.Amount);
            
        var spendLastMonth = expenses
            .Where(t => t.TransactionDate.Date >= startOfLastMonth && t.TransactionDate.Date < startOfThisMonth)
            .Sum(t => t.Amount);

        // 3. Linear Regression for Month-End Projection
        decimal projectedMonthEnd = spendThisMonth;
        var daysPassed = (now - startOfThisMonth).TotalDays;
        var daysInMonth = DateTime.DaysInMonth(now.Year, now.Month);
        
        if (daysPassed >= 1)
        {
            projectedMonthEnd = (spendThisMonth / (decimal)daysPassed) * daysInMonth;
        }

        // 4. Daily Aggregation & Burn Rate Calculation
        // CRITICAL FIX: To calculate daily burn correctly, we must consider the full window size, not just days with spend.
        var analysisWindowDays = settings.AnalysisWindowDays > 0 ? settings.AnalysisWindowDays : 90;
        var windowStartDate = DateTime.UtcNow.AddDays(-analysisWindowDays).Date;

        var validExpenses = expenses.Where(t => t.TransactionDate.Date >= windowStartDate).ToList();
        var totalWindowSpend = validExpenses.Sum(t => t.Amount);
        
        // Average Daily Spend = Total Spend / Window Days
        var avgDailySpend = (double)totalWindowSpend / analysisWindowDays;

        var dailyExpensesMap = validExpenses
            .GroupBy(t => t.TransactionDate.Date)
            .ToDictionary(g => g.Key, g => (double)g.Sum(t => t.Amount));

        // Calculate Standard Deviation (Volatility) considering ALL days in the window (including zeros)
        double sumSquaredDiff = 0;
        for (int i = 0; i < analysisWindowDays; i++)
        {
            var date = windowStartDate.AddDays(i);
            var dailySpend = dailyExpensesMap.ContainsKey(date) ? dailyExpensesMap[date] : 0.0;
            sumSquaredDiff += Math.Pow(dailySpend - avgDailySpend, 2);
        }
        var stdDev = Math.Sqrt(sumSquaredDiff / analysisWindowDays);

        // 6. Exponential Moving Average (EMA) - "Weighted Burn"
        // We iterate through every day in the window to let the EMA decay on zero-spend days
        decimal weightedBurn = (decimal)avgDailySpend; // Start with simple average
        decimal alpha = settings.ActuarialAlpha; 
        
        for (int i = 0; i < analysisWindowDays; i++)
        {
            var date = windowStartDate.AddDays(i);
            var dailySpend = (decimal)(dailyExpensesMap.ContainsKey(date) ? dailyExpensesMap[date] : 0.0);
            weightedBurn = (dailySpend * alpha) + (weightedBurn * (1 - alpha));
        }

        // 7. Runway Scenarios
        var baseBurn = weightedBurn > 0 ? weightedBurn : (decimal)avgDailySpend; // Fallback to simple average
        if (baseBurn == 0) baseBurn = 1; // Prevent div/0

        var expectedRunway = currentBalance / baseBurn;
        var stressBurn = (decimal)((double)baseBurn + stdDev); 
        var safeRunway = stressBurn > 0 ? currentBalance / stressBurn : 0;
        var lowBurn = (decimal)Math.Max((double)baseBurn - stdDev, 1.0);
        var optimisticRunway = currentBalance / lowBurn;

        // 8. Value at Risk (VaR 95%)
        var maxProbableDailySpend = avgDailySpend + (1.645 * stdDev);

        // 9. Trend Analysis
        var trend = "Stable";
        // Compare last 30 days vs previous 30 days within the window
        if (analysisWindowDays >= 60)
        {
            var period1Start = windowStartDate;
            var period2Start = windowStartDate.AddDays(30);
            
            var period1Spend = validExpenses.Where(t => t.TransactionDate.Date >= period1Start && t.TransactionDate.Date < period2Start).Sum(t => t.Amount);
            var period2Spend = validExpenses.Where(t => t.TransactionDate.Date >= period2Start && t.TransactionDate.Date < period2Start.AddDays(30)).Sum(t => t.Amount);
            
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