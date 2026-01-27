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
    string TrendDirection // "Stable", "Deteriorating", "Improving"
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
        // Get settings (blocking for simplicity in this synchronous logic, or we could make interface async)
        // Since we are inside a heavy calculation, fetching settings is negligible.
        var settings = _settingsService.GetSettingsAsync().GetAwaiter().GetResult();

        // Filter for expenses only (negative amounts) and group by day
        var dailyExpenses = history
            .Where(t => t.Amount < 0)
            .GroupBy(t => t.TransactionDate.Date)
            .Select(g => Math.Abs(g.Sum(t => t.Amount)))
            .ToList();

        if (dailyExpenses.Count == 0)
        {
            return new FinancialHealthReport(currentBalance, 0, 0, 0, 0, 0, 0, 0, "No Data");
        }

        // 1. Calculate Basic Statistics (Mean & StdDev)
        var n = dailyExpenses.Count;
        var avgDailySpend = dailyExpenses.Average();
        var sumOfSquares = dailyExpenses.Sum(val => Math.Pow((double)(val - avgDailySpend), 2));
        var stdDev = Math.Sqrt(sumOfSquares / n); // Population StdDev approximation

        // 2. Exponential Moving Average (EMA) for "Weighted Burn"
        // Give more weight to recent spending. alpha = 2 / (N+1). Using N=30 days roughly.
        decimal weightedBurn = 0;
        decimal alpha = settings.ActuarialAlpha; 
        // Sort chronologically for EMA
        var orderedExpenses = history
             .Where(t => t.Amount < 0)
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

        // 3. Stress Testing (VaR - Value at Risk)
        // What is the max we might spend in a day with 95% confidence? (Z = 1.645)
        var zScore95 = 1.645;
        var maxProbableDailySpend = (double)avgDailySpend + (zScore95 * stdDev);
        
        // 4. Runway Scenarios
        // Base calculation on the Weighted Burn (more reactive) vs Pure Average
        var baseBurn = weightedBurn > 0 ? weightedBurn : 1; // Avoid divide by zero

        var expectedRunway = currentBalance / baseBurn;
        
        // Conservative: Assume high volatility days happen frequently
        var stressBurn = (decimal)((double)baseBurn + stdDev); 
        var safeRunway = stressBurn > 0 ? currentBalance / stressBurn : 0;

        // Optimistic: Low volatility
        var lowBurn = (decimal)Math.Max((double)baseBurn - stdDev, 1.0);
        var optimisticRunway = currentBalance / lowBurn;

        // 5. Trend Analysis (Linear Regression on Balance)
        // We need balance snapshots. If not available directly, we reconstruct from transactions is harder.
        // Instead, check the slope of the daily spend.
        var trend = "Stable";
        if (orderedExpenses.Count > 10)
        {
            var firstHalf = orderedExpenses.Take(orderedExpenses.Count / 2).Average();
            var secondHalf = orderedExpenses.Skip(orderedExpenses.Count / 2).Average();
            var change = (secondHalf - firstHalf) / (firstHalf == 0 ? 1 : firstHalf);
            
            if (change > 0.1m) trend = "Deteriorating (Spending Increasing)";
            else if (change < -0.1m) trend = "Improving (Spending Decreasing)";
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
            TrendDirection: trend
        );
    }
}
