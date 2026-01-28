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
    decimal ProjectedBalanceAtNextSalary,
    decimal UpcomingExpectedPayments,
    int DaysUntilNextSalary,
    double RunwayProbability,
    List<CategorySpend> TopCategories,
    List<UpcomingExpense> UpcomingFixedCosts
);

public record UpcomingExpense(string Name, decimal ExpectedAmount);

public record CategorySpend(string Name, decimal Amount, decimal ChangeAmount, decimal ChangePercentage, bool IsStable, bool IsFixedCost);

public interface IActuarialService
{
    Task<FinancialHealthReport> AnalyzeHealthAsync(List<Transaction> history, decimal currentBalance);
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
        string[] months = { "JANUARY", "FEBRUARY", "MARCH", "APRIL", "MAY", "JUNE", "JULY", "AUGUST", "SEPTEMBER", "OCTOBER", "NOVEMBER", "DECEMBER", "JAN", "FEB", "MAR", "APR", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC" };
        var clean = desc.ToUpper();
        clean = Regex.Replace(clean, @"\d|ZA|CPT|JHB|GP|GAUTENG|PTY|LTD|\*|'|&APOS;|DEBIT|ORDER|PAYMENT|INSTALMENT|EFT|MAG|TAPE|ELECTRONIC|FUNDS|TRANSFER|INT-ACC|INTERNAL", " ").Trim();
        foreach (var m in months) clean = Regex.Replace(clean, $@"\b{m}\b", " ");
        var parts = clean.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? (parts.Length > 1 ? $"{parts[0]} {parts[1]}" : parts[0]).ToUpper() : "Uncategorized";
    }

    private bool IsFixedCost(string categoryName)
    {
        string[] fixedKeywords = { "SCHOOL", "MORTGAGE", "LEVIES", "HOME LOAN", "INSURANCE", "BOND", "INVESTMENT", "LIFE", "MEDICAL", "NEDBHL", "DISC PREM", "WILLOWBROOKE", "ADAM" };
        return fixedKeywords.Any(k => categoryName.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    private bool IsSalary(Transaction t)
    {
        if (t.Description == null) return false;
        return t.Description.Contains("TCP 131", StringComparison.OrdinalIgnoreCase) || 
               t.Description.Contains("TCP131", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<FinancialHealthReport> AnalyzeHealthAsync(List<Transaction> history, decimal currentBalance)
    {
        var settings = await _settingsService.GetSettingsAsync();
        var today = DateTime.Today;

        // SALARY DETECTION (Sign-Agnostic)
        var salaryPayments = history
            .Where(t => IsSalary(t))
            .OrderByDescending(t => t.TransactionDate)
            .ToList();

        // Fallback: If no explicit salary, look for large credit/deposit
        if (!salaryPayments.Any())
        {
            salaryPayments = history
                .Where(t => (t.Amount < -10000 || t.Category == "CREDIT") && (t.TransactionDate.LocalDateTime.Date >= today.AddDays(-45)))
                .OrderByDescending(t => Math.Abs(t.Amount))
                .Take(1)
                .ToList();
        }

        DateTime periodStart; DateTime prevPeriodStart; DateTime prevPeriodEnd;

        if (salaryPayments.Any())
        {
            periodStart = salaryPayments[0].TransactionDate.LocalDateTime.Date;
            if (salaryPayments.Count >= 2)
            {
                var prevSalary = salaryPayments.FirstOrDefault(s => s.TransactionDate.LocalDateTime.Date < periodStart);
                if (prevSalary != null) { prevPeriodStart = prevSalary.TransactionDate.LocalDateTime.Date; prevPeriodEnd = periodStart; }
                else { prevPeriodStart = periodStart.AddDays(-30); prevPeriodEnd = periodStart; }
            }
            else { prevPeriodStart = periodStart.AddDays(-30); prevPeriodEnd = periodStart; }
        }
        else 
        { 
            // Fallback to a rolling 28-day cycle if no salary detected
            periodStart = today.AddDays(-7); // Assuming user said paid 1 week ago
            prevPeriodStart = periodStart.AddDays(-30); 
            prevPeriodEnd = periodStart; 
        }

        var daysIntoPeriod = Math.Max(1, (today - periodStart).TotalDays + 1);
        
        // Calculate average cycle from history or default to 30
        var cycleHistory = salaryPayments.Zip(salaryPayments.Skip(1), (a, b) => (a.TransactionDate - b.TransactionDate).TotalDays).ToList();
        var avgCycleDays = cycleHistory.Any() ? cycleHistory.Average() : 30;
        if (avgCycleDays < 20 || avgCycleDays > 45) avgCycleDays = 30; 

        var nextExpectedSalary = periodStart.AddDays(avgCycleDays);
        var daysUntilNextSalary = Math.Max(1, (nextExpectedSalary - today).TotalDays);

        var compareDateInPrevPeriod = prevPeriodStart.AddDays(daysIntoPeriod);
        if (compareDateInPrevPeriod > prevPeriodEnd) compareDateInPrevPeriod = prevPeriodEnd;

        // Investec: Debits are POSITIVE (> 0), Credits are NEGATIVE (< 0). 
        var expenses = history.Where(t => t.Amount > 0 && 
                                        !string.Equals(t.Category, "CREDIT", StringComparison.OrdinalIgnoreCase) && 
                                        !t.IsInternalTransfer()).ToList();
        
        DateTime ToDate(DateTimeOffset dto) => dto.LocalDateTime.Date;
        var thisPeriodExpenses = expenses.Where(t => ToDate(t.TransactionDate) >= periodStart).ToList(); 
        var lastPeriodFullExpenses = expenses.Where(t => ToDate(t.TransactionDate) >= prevPeriodStart && ToDate(t.TransactionDate) < prevPeriodEnd).ToList();
        var lastPeriodPtdExpenses = expenses.Where(t => ToDate(t.TransactionDate) >= prevPeriodStart && ToDate(t.TransactionDate) < compareDateInPrevPeriod).ToList();

        var spendThisPeriod = thisPeriodExpenses.Sum(t => t.Amount);
        var spendLastPeriodPtd = lastPeriodPtdExpenses.Sum(t => t.Amount); // Use PTD forPulse comparison

        var topCategories = thisPeriodExpenses
            .Where(t => !IsFixedCost(NormalizeDescription(t.Description))) // ONLY show unexpected/variable categories
            .GroupBy(t => NormalizeDescription(t.Description))
            .Select(g => new { Name = g.Key, Amount = g.Sum(t => t.Amount) })
            .OrderByDescending(x => x.Amount).Take(5).ToList();

        var categoryReport = new List<CategorySpend>();
        foreach (var cat in topCategories)
        {
            var ptdLastSpend = lastPeriodPtdExpenses.Where(t => NormalizeDescription(t.Description) == cat.Name).Sum(t => t.Amount);
            
            // Strictly like-for-like PTD comparison
            decimal diff = cat.Amount - ptdLastSpend;
            decimal percent = ptdLastSpend > 0 ? (diff / ptdLastSpend) * 100 : 100;

                        bool isStable = Math.Abs(percent) < 10 || Math.Abs(diff) < 100;
                        categoryReport.Add(new CategorySpend(cat.Name, cat.Amount, diff, percent, isStable, IsFixedCost(cat.Name)));                                                                                                           
                    }
        // Identify Upcoming Expected Payments
        var upcomingFixedCosts = new List<UpcomingExpense>();
        var historicalFixedExpenses = lastPeriodFullExpenses.Where(t => IsFixedCost(NormalizeDescription(t.Description)))
            .GroupBy(t => NormalizeDescription(t.Description)).ToList();

        foreach (var group in historicalFixedExpenses)
        {
            if (!thisPeriodExpenses.Any(t => NormalizeDescription(t.Description) == group.Key))
            {
                upcomingFixedCosts.Add(new UpcomingExpense(group.Key, group.Average(t => t.Amount)));
            }
        }
        decimal upcomingOverhead = upcomingFixedCosts.Sum(e => e.ExpectedAmount);

        var analysisWindowDays = settings.AnalysisWindowDays > 0 ? settings.AnalysisWindowDays : 90;
        var windowStartDate = today.AddDays(-analysisWindowDays);
        var validExpenses = expenses.Where(t => ToDate(t.TransactionDate) >= windowStartDate).OrderBy(t => t.TransactionDate).ToList();
        var dailyExpensesMap = validExpenses.GroupBy(t => ToDate(t.TransactionDate)).ToDictionary(g => g.Key, g => (double)g.Sum(t => t.Amount));

        decimal alpha = settings.ActuarialAlpha > 0 ? settings.ActuarialAlpha : 0.15m;
        double weightedMean = 0; double weightedVar = 0; bool initialized = false;
        for (int i = 0; i < analysisWindowDays; i++)
        {
            var loopDate = windowStartDate.AddDays(i);
            double dailySpend = dailyExpensesMap.TryGetValue(loopDate, out var value) ? value : 0.0;
            if (!initialized) { weightedMean = dailySpend; initialized = true; }
            else { double delta = dailySpend - weightedMean; weightedMean += (double)alpha * delta; weightedVar = (1 - (double)alpha) * (weightedVar + (double)alpha * Math.Pow(delta, 2)); }
        }

        var stdDev = Math.Sqrt(weightedVar);
        var baseBurn = (decimal)weightedMean > 0 ? (decimal)weightedMean : 1m;
        double probSurvival = 0;
        if (stdDev > 0)
        {
            var meanToPayday = (double)baseBurn * daysUntilNextSalary;
            var stdDevToPayday = stdDev * Math.Sqrt(daysUntilNextSalary);
            probSurvival = CumulativeDistributionFunction(((double)currentBalance - (double)upcomingOverhead - meanToPayday) / stdDevToPayday) * 100;
        }
        else { probSurvival = (currentBalance - upcomingOverhead) > (baseBurn * (decimal)daysUntilNextSalary) ? 100 : 0; }

        var dailyAvgSimple = validExpenses.Any() ? (double)validExpenses.Sum(t => t.Amount) / analysisWindowDays : 0;
        decimal projectedSpend = (spendThisPeriod / (decimal)daysIntoPeriod) * (decimal)avgCycleDays;
        decimal projectedBalance = currentBalance - upcomingOverhead - (baseBurn * (decimal)daysUntilNextSalary);

        return new FinancialHealthReport(
            CurrentBalance: currentBalance,
            WeightedDailyBurn: baseBurn,
            MonthlyBurnRate: baseBurn * 30,
            BurnVolatility: stdDev,
            SafeRunwayDays: (currentBalance - upcomingOverhead) / (decimal)((double)baseBurn + stdDev),
            ExpectedRunwayDays: (currentBalance - upcomingOverhead) / baseBurn,
            OptimisticRunwayDays: (currentBalance - upcomingOverhead) / (decimal)Math.Max((double)baseBurn - stdDev, 1.0),
            ValueAtRisk95: (decimal)(weightedMean + (1.645 * stdDev)),
            TrendDirection: (decimal)weightedMean > (decimal)dailyAvgSimple * 1.1m ? "Increasing" : ((decimal)weightedMean < (decimal)dailyAvgSimple * 0.9m ? "Decreasing" : "Stable"),
            SpendThisMonth: spendThisPeriod,
            SpendLastMonth: spendLastPeriodPtd,
            ProjectedMonthEndSpend: projectedSpend + upcomingOverhead,
            ProjectedBalanceAtNextSalary: projectedBalance,
            UpcomingExpectedPayments: upcomingOverhead,
            DaysUntilNextSalary: (int)Math.Round(daysUntilNextSalary),
            RunwayProbability: Math.Min(Math.Max(probSurvival, 0), 100),
            TopCategories: categoryReport.OrderByDescending(c => c.Amount).Take(3).ToList(),
            UpcomingFixedCosts: upcomingFixedCosts
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
