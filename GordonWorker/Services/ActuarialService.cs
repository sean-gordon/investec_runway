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
    int DaysUntilNextSalary,
    double RunwayProbability,
    List<CategorySpend> TopCategories
);

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
        
        foreach (var m in months) {
            clean = Regex.Replace(clean, $@"\b{m}\b", " ");
        }

        var parts = clean.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? (parts.Length > 1 ? $"{parts[0]} {parts[1]}" : parts[0]).ToUpper() : "Uncategorized";
    }

    private bool IsFixedCost(string categoryName)
    {
        string[] fixedKeywords = { "SCHOOL", "MORTGAGE", "LEVIES", "HOME LOAN", "INSURANCE", "BOND", "INVESTMENT", "LIFE", "MEDICAL", "NEDBHL", "DISC PREM" };
        return fixedKeywords.Any(k => categoryName.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<FinancialHealthReport> AnalyzeHealthAsync(List<Transaction> history, decimal currentBalance)
    {
        var settings = await _settingsService.GetSettingsAsync();

        var salaryPayments = history
            .Where(t => t.Description != null && (t.Description.Contains("TCP 131", StringComparison.OrdinalIgnoreCase) || t.Description.Contains("TCP131", StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(t => t.TransactionDate)
            .ToList();

        DateTime periodStart;
        DateTime prevPeriodStart;
        DateTime prevPeriodEnd;

        var today = DateTime.Today;

        if (salaryPayments.Count >= 1)
        {
            periodStart = salaryPayments[0].TransactionDate.LocalDateTime.Date;
            if (salaryPayments.Count >= 2)
            {
                var prevSalary = salaryPayments.FirstOrDefault(s => s.TransactionDate.LocalDateTime.Date < periodStart);
                if (prevSalary != null)
                {
                    prevPeriodStart = prevSalary.TransactionDate.LocalDateTime.Date;
                    prevPeriodEnd = periodStart;
                }
                else
                {
                    prevPeriodStart = periodStart.AddMonths(-1);
                    prevPeriodEnd = periodStart;
                }
            }
            else
            {
                prevPeriodStart = periodStart.AddMonths(-1);
                prevPeriodEnd = periodStart;
            }
        }
        else
        {
            periodStart = new DateTime(today.Year, today.Month, 1);
            prevPeriodStart = periodStart.AddMonths(-1);
            prevPeriodEnd = periodStart;
        }

        var daysIntoPeriod = (today - periodStart).TotalDays + 1;
        if (daysIntoPeriod < 1) daysIntoPeriod = 1;

        var compareDateInPrevPeriod = prevPeriodStart.AddDays(daysIntoPeriod);
        if (compareDateInPrevPeriod > prevPeriodEnd) compareDateInPrevPeriod = prevPeriodEnd;

        var expenses = history
            .Where(t => t.Amount > 0 && 
                        !string.Equals(t.Category, "CREDIT", StringComparison.OrdinalIgnoreCase) &&      
                        !t.IsInternalTransfer())
            .ToList();
        
        DateTime ToDate(DateTimeOffset dto) => dto.LocalDateTime.Date;

        var thisPeriodExpenses = expenses.Where(t => ToDate(t.TransactionDate) >= periodStart).ToList(); 
        var lastPeriodFullExpenses = expenses.Where(t => ToDate(t.TransactionDate) >= prevPeriodStart && ToDate(t.TransactionDate) < prevPeriodEnd).ToList();
        var lastPeriodPtdExpenses = expenses.Where(t => ToDate(t.TransactionDate) >= prevPeriodStart && ToDate(t.TransactionDate) < compareDateInPrevPeriod).ToList();

        var spendThisMonth = thisPeriodExpenses.Sum(t => t.Amount);
        var spendLastMonth = lastPeriodFullExpenses.Sum(t => t.Amount);

        var topCategories = thisPeriodExpenses
            .GroupBy(t => NormalizeDescription(t.Description))
            .Select(g => new { Name = g.Key, Amount = g.Sum(t => t.Amount) })
            .OrderByDescending(x => x.Amount)
            .Take(5)
            .ToList();

        var categoryReport = new List<CategorySpend>();
        foreach (var cat in topCategories)
        {
            var ptdLastPeriodCatSpend = lastPeriodPtdExpenses.Where(t => NormalizeDescription(t.Description) == cat.Name).Sum(t => t.Amount);
            var fullLastPeriodCatSpend = lastPeriodFullExpenses.Where(t => NormalizeDescription(t.Description) == cat.Name).Sum(t => t.Amount);
            
            decimal diff = 0;
            decimal percent = 0;

            decimal ptdPercent = ptdLastPeriodCatSpend > 0 ? ((cat.Amount - ptdLastPeriodCatSpend) / ptdLastPeriodCatSpend) * 100 : 100;
            decimal fullPercent = fullLastPeriodCatSpend > 0 ? ((cat.Amount - fullLastPeriodCatSpend) / fullLastPeriodCatSpend) * 100 : 100;

            // Logic Fix: If we find a match in the FULL period that is reasonably close, treat it as the baseline to avoid timing shifts
            if (fullLastPeriodCatSpend > 0 && Math.Abs(fullPercent) < 15)
            {
                percent = fullPercent;
                diff = cat.Amount - fullLastPeriodCatSpend;
            }
            else
            {
                percent = ptdPercent;
                diff = cat.Amount - ptdLastPeriodCatSpend;
            }

            // A category is 'stable' if variance < 10% (accounts for school fee increases) OR absolute diff < R100
            bool isStable = Math.Abs(percent) < 10 || Math.Abs(diff) < 100;
            categoryReport.Add(new CategorySpend(cat.Name, cat.Amount, diff, percent, isStable, IsFixedCost(cat.Name)));        
        }

        var cycleHistory = salaryPayments.Zip(salaryPayments.Skip(1), (a, b) => (a.TransactionDate - b.TransactionDate).TotalDays).ToList();
        var avgCycleDays = cycleHistory.Any() ? cycleHistory.Average() : 30;
        if (avgCycleDays < 20 || avgCycleDays > 45) avgCycleDays = 30; 

        var nextExpectedSalary = periodStart.AddDays(avgCycleDays);
        var daysUntilNextSalary = (nextExpectedSalary - today).TotalDays;
        if (daysUntilNextSalary < 1) daysUntilNextSalary = 1;

        var analysisWindowDays = settings.AnalysisWindowDays > 0 ? settings.AnalysisWindowDays : 90;
        var windowStartDate = today.AddDays(-analysisWindowDays);
        var validExpenses = expenses.Where(t => ToDate(t.TransactionDate) >= windowStartDate).OrderBy(t => t.TransactionDate).ToList();

        var dailyExpensesMap = validExpenses.GroupBy(t => ToDate(t.TransactionDate)).ToDictionary(g => g.Key, g => (double)g.Sum(t => t.Amount));

        decimal alpha = settings.ActuarialAlpha > 0 ? settings.ActuarialAlpha : 0.15m;
        double weightedMean = 0;
        double weightedVar = 0;
        bool initialized = false;

        for (int i = 0; i < analysisWindowDays; i++)
        {
            var loopDate = windowStartDate.AddDays(i);
            double dailySpend = dailyExpensesMap.TryGetValue(loopDate, out var value) ? value : 0.0;
            if (!initialized) { weightedMean = dailySpend; initialized = true; }
            else
            {
                double delta = dailySpend - weightedMean;
                weightedMean += (double)alpha * delta;
                weightedVar = (1 - (double)alpha) * (weightedVar + (double)alpha * Math.Pow(delta, 2));
            }
        }

        var stdDev = Math.Sqrt(weightedVar);
        var baseBurn = (decimal)weightedMean > 0 ? (decimal)weightedMean : 1m;

        double probSurvival = 0;
        if (stdDev > 0)
        {
            var meanToPayday = (double)baseBurn * daysUntilNextSalary;
            var stdDevToPayday = stdDev * Math.Sqrt(daysUntilNextSalary);
            var zScore = ((double)currentBalance - meanToPayday) / stdDevToPayday;
            probSurvival = CumulativeDistributionFunction(zScore) * 100;
        }
        else { probSurvival = currentBalance > (baseBurn * (decimal)daysUntilNextSalary) ? 100 : 0; }

        var dailyAvgSimple = validExpenses.Any() ? (double)validExpenses.Sum(t => t.Amount) / analysisWindowDays : 0;
        decimal projectedMonthEnd = spendThisMonth;
        if (daysIntoPeriod >= 1) projectedMonthEnd = (spendThisMonth / (decimal)daysIntoPeriod) * (decimal)avgCycleDays;

        decimal remainingSpendExpected = (decimal)weightedMean * (decimal)daysUntilNextSalary;
        decimal projectedBalanceAtPayday = currentBalance - remainingSpendExpected;

        return new FinancialHealthReport(
            CurrentBalance: currentBalance,
            WeightedDailyBurn: baseBurn,
            MonthlyBurnRate: baseBurn * 30,
            BurnVolatility: stdDev,
            SafeRunwayDays: currentBalance / (decimal)((double)baseBurn + stdDev),
            ExpectedRunwayDays: currentBalance / baseBurn,
            OptimisticRunwayDays: currentBalance / (decimal)Math.Max((double)baseBurn - stdDev, 1.0),
            ValueAtRisk95: (decimal)(weightedMean + (1.645 * stdDev)),
            TrendDirection: (decimal)weightedMean > (decimal)dailyAvgSimple * 1.1m ? "Increasing" : ((decimal)weightedMean < (decimal)dailyAvgSimple * 0.9m ? "Decreasing" : "Stable"),
            SpendThisMonth: spendThisMonth,
            SpendLastMonth: spendLastMonth,
            ProjectedMonthEndSpend: projectedMonthEnd,
            ProjectedBalanceAtNextSalary: projectedBalanceAtPayday,
            DaysUntilNextSalary: (int)Math.Round(daysUntilNextSalary),
            RunwayProbability: Math.Min(Math.Max(probSurvival, 0), 100),
            TopCategories: categoryReport.OrderByDescending(c => c.Amount).Take(3).ToList()
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
