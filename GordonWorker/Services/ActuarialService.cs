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
        
        // 2. Monthly Stats
        var now = DateTime.UtcNow;
        var startOfThisMonth = new DateTime(now.Year, now.Month, 1);
        var startOfLastMonth = startOfThisMonth.AddMonths(-1);
        
        // Compare purely on Date component to avoid TimeZone issues
        var spendThisMonth = expenses
            .Where(t => t.TransactionDate.Date >= startOfThisMonth.Date)
            .Sum(t => t.Amount);
            
        var spendLastMonth = expenses
            .Where(t => t.TransactionDate.Date >= startOfLastMonth.Date && t.TransactionDate.Date < startOfThisMonth.Date)
            .Sum(t => t.Amount);

        // 3. Linear Regression for Month-End Projection
        decimal projectedMonthEnd = spendThisMonth;
        var daysPassed = (now - startOfThisMonth).TotalDays;
        var daysInMonth = DateTime.DaysInMonth(now.Year, now.Month);
        
        // Avoid divide by zero or projecting on day 0
        if (daysPassed >= 1)
        {
            projectedMonthEnd = (spendThisMonth / (decimal)daysPassed) * daysInMonth;
        }

        // 4. Daily Aggregation for Volatility & Burn
        var dailyExpenses = expenses
            .GroupBy(t => t.TransactionDate.Date)
            .Select(g => Math.Abs(g.Sum(t => t.Amount)))
            .ToList();

        if (dailyExpenses.Count == 0)
        {
            return new FinancialHealthReport(currentBalance, 0, 0, 0, 0, 0, 0, 0, "No Data", 0, 0, 0, 0);
        }

        // 5. Statistical Analysis (Mean & StdDev)
        var n = dailyExpenses.Count;
        var avgDailySpend = dailyExpenses.Average();
        var sumOfSquares = dailyExpenses.Sum(val => Math.Pow((double)(val - avgDailySpend), 2));
        var stdDev = Math.Sqrt(sumOfSquares / n); 

        // 6. Exponential Moving Average (EMA) - "Weighted Burn"
        decimal weightedBurn = 0;
        decimal alpha = settings.ActuarialAlpha; 
        var orderedExpenses = expenses
             .GroupBy(t => t.TransactionDate.Date)
             .OrderBy(g => g.Key)
             .Select(g => Math.Abs(g.Sum(t => t.Amount)))
             .ToList();

        if (orderedExpenses.Any())
        {
            weightedBurn = orderedExpenses.First();
            foreach (var expense in orderedExpenses.Skip(1))
            {
                weightedBurn = (expense * alpha) + (weightedBurn * (1 - alpha));
            }
        }

        // 7. Runway Scenarios
        var baseBurn = weightedBurn > 0 ? weightedBurn : 1; 
        var expectedRunway = currentBalance / baseBurn;
        
        // Conservative (High Volatility impact)
        var stressBurn = (decimal)((double)baseBurn + stdDev); 
        var safeRunway = stressBurn > 0 ? currentBalance / stressBurn : 0;

        // Optimistic
        var lowBurn = (decimal)Math.Max((double)baseBurn - stdDev, 1.0);
        var optimisticRunway = currentBalance / lowBurn;

        // 8. Value at Risk (VaR 95%) - One-tailed Z=1.645
        var maxProbableDailySpend = (double)avgDailySpend + (1.645 * stdDev);

        // 9. Trend Analysis (Slope of last 30 days)
        var trend = "Stable";
        if (orderedExpenses.Count > 14)
        {
            var recent = orderedExpenses.TakeLast(14).Average();
            var prior = orderedExpenses.SkipLast(14).TakeLast(14).Average();
            var change = (recent - prior) / (prior == 0 ? 1 : prior);
            
            if (change > 0.1m) trend = "Deteriorating (Spending Accelerating)";
            else if (change < -0.1m) trend = "Improving (Spending Decelerating)";
        }

        // 10. Monte Carlo Simulation for Probability (Simplified)
        // Probability that balance > 0 after 30 days given Mean and StdDev
        // This is effectively calculating Z-score of (CurrentBalance / 30) against the daily distribution
        // If DailySpend ~ N(Mean, StdDev), then 30-Day Spend ~ N(30*Mean, Sqrt(30)*StdDev)
        // We want P(30-Day Spend < CurrentBalance)
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