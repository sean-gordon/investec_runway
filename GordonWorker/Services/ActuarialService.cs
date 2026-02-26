using GordonWorker.Models;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

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
    decimal SpendSameMonthLastYear,
    decimal YoYChangePercentage,
    decimal ProjectedMonthEndSpend,
    decimal ProjectedBalanceAtNextSalary,
    decimal UpcomingExpectedPayments,
    decimal LastDetectedSalaryAmount,
    int DaysUntilNextSalary,
    double RunwayProbability,
    List<CategorySpend> TopCategories,
    List<UpcomingExpense> UpcomingFixedCosts
);

public record UpcomingExpense(string Name, decimal ExpectedAmount);

public record CategorySpend(string Name, decimal Amount, decimal ChangeAmount, decimal ChangePercentage, bool IsStable, bool IsFixedCost);

public interface IActuarialService
{
    Task<FinancialHealthReport> AnalyzeHealthAsync(List<Transaction> history, decimal currentBalance, AppSettings settings);
    bool IsSalary(Transaction t, AppSettings settings);
    string NormalizeDescription(string? desc);
    bool IsFixedCost(string categoryName, AppSettings settings);
}

public class ActuarialService : IActuarialService
{
    private readonly ILogger<ActuarialService> _logger;

    public ActuarialService(ILogger<ActuarialService> logger)
    {
        _logger = logger;
    }

    public string NormalizeDescription(string? desc)
    {
        if (string.IsNullOrWhiteSpace(desc)) return "Uncategorized";
        string[] months = { "JANUARY", "FEBRUARY", "MARCH", "APRIL", "MAY", "JUNE", "JULY", "AUGUST", "SEPTEMBER", "OCTOBER", "NOVEMBER", "DECEMBER", "JAN", "FEB", "MAR", "APR", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC" };
        var clean = desc.ToUpper();
        clean = Regex.Replace(clean, @"\d|ZA|CPT|JHB|GP|GAUTENG|PTY|LTD|\*|'|&APOS;|DEBIT|ORDER|PAYMENT|INSTALMENT|EFT|MAG|TAPE|ELECTRONIC|FUNDS|TRANSFER|INT-ACC|INTERNAL", " ").Trim();
        foreach (var m in months) clean = Regex.Replace(clean, $@"\b{m}\b", " ");
        var parts = clean.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? (parts.Length > 1 ? $"{parts[0]} {parts[1]}" : parts[0]).ToUpper() : "Uncategorized";
    }

    public bool IsFixedCost(string categoryName, AppSettings settings)
    {
        var keywords = settings.FixedCostKeywords.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return keywords.Any(k => categoryName.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    public bool IsSalary(Transaction t, AppSettings settings)
    {
        if (t.Description == null) return false;
        var keywords = settings.SalaryKeywords.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return keywords.Any(k => t.Description.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<FinancialHealthReport> AnalyzeHealthAsync(List<Transaction> history, decimal currentBalance, AppSettings settings)
    {
        var today = DateTime.Today;
        _logger.LogInformation("[Actuarial] Starting analysis. History: {Count} transactions, Balance: {Balance:F2}", history.Count, currentBalance);

        // SALARY DETECTION (Sign-Agnostic)
        var salaryPayments = history
            .Where(t => IsSalary(t, settings))
            .OrderByDescending(t => t.TransactionDate)
            .ToList();

        // Fallback: If no explicit salary, look for large credit/deposit
        if (!salaryPayments.Any())
        {
            salaryPayments = history
                .Where(t => (t.Amount > settings.SalaryFallbackThreshold || 
                             string.Equals(t.Category, "CREDIT", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(t.Category, "Income", StringComparison.OrdinalIgnoreCase)) && 
                            (t.TransactionDate.LocalDateTime.Date >= today.AddDays(-settings.SalaryFallbackDays)))
                .OrderByDescending(t => t.Amount)
                .Take(1)
                .ToList();
            if (salaryPayments.Any())
                _logger.LogInformation("[Actuarial] Salary: not found by keyword — fallback to largest credit: {Desc} (R{Amount:F2})", salaryPayments[0].Description, Math.Abs(salaryPayments[0].Amount));
            else
                _logger.LogWarning("[Actuarial] Salary: no salary detected by keyword or fallback. Using rolling 28-day period.");
        }
        else
        {
            _logger.LogInformation("[Actuarial] Salary: detected {Count} salary payment(s). Latest: {Desc} on {Date:yyyy-MM-dd} (R{Amount:F2})",
                salaryPayments.Count, salaryPayments[0].Description, salaryPayments[0].TransactionDate.LocalDateTime, Math.Abs(salaryPayments[0].Amount));
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
        var avgCycleDays = cycleHistory.Any() ? cycleHistory.Average() : settings.DefaultCycleDays;
        
        // Clamp to min/max range first
        if (avgCycleDays < settings.MinCycleDays) avgCycleDays = settings.MinCycleDays;
        else if (avgCycleDays > settings.MaxCycleDays) avgCycleDays = settings.MaxCycleDays;

        var nextExpectedSalary = periodStart.AddDays(avgCycleDays);
        var daysUntilNextSalary = Math.Max(1, (nextExpectedSalary - today).TotalDays);

        var compareDateInPrevPeriod = prevPeriodStart.AddDays(daysIntoPeriod);
        if (compareDateInPrevPeriod > prevPeriodEnd) compareDateInPrevPeriod = prevPeriodEnd;

        // Debits are NEGATIVE (< 0), Credits are POSITIVE (> 0). 
        var internalTransfers = history.Where(t => t.IsInternalTransfer()).ToList();
        var expenses = history.Where(t => t.Amount < 0 && !t.IsInternalTransfer())
                              .Select(t => new Transaction {
                                  Id = t.Id, AccountId = t.AccountId, TransactionDate = t.TransactionDate,
                                  Description = t.Description, Amount = Math.Abs(t.Amount), Balance = t.Balance,
                                  Category = t.Category, IsAiProcessed = t.IsAiProcessed, Notes = t.Notes
                              }).ToList();
                              
        _logger.LogInformation("[Actuarial] Expense filter: {Total} total txns → {ExpenseCount} expenses, {CreditCount} credits/income, {TransferCount} internal transfers excluded",
            history.Count, expenses.Count,
            history.Count(t => t.Amount > 0 && !t.IsInternalTransfer()),
            internalTransfers.Count);
        if (internalTransfers.Any())
            _logger.LogDebug("[Actuarial] Internal transfers excluded: {Descriptions}",
                string.Join(", ", internalTransfers.Select(t => t.Description).Distinct().Take(10)));
        
        DateTime ToDate(DateTimeOffset dto) => dto.LocalDateTime.Date;
        var thisPeriodExpenses = expenses.Where(t => ToDate(t.TransactionDate) >= periodStart).ToList(); 
        var lastPeriodFullExpenses = expenses.Where(t => ToDate(t.TransactionDate) >= prevPeriodStart && ToDate(t.TransactionDate) < prevPeriodEnd).ToList();
        var lastPeriodPtdExpenses = expenses.Where(t => ToDate(t.TransactionDate) >= prevPeriodStart && ToDate(t.TransactionDate) < compareDateInPrevPeriod).ToList();
        _logger.LogInformation("[Actuarial] Period: start={PeriodStart:yyyy-MM-dd}, daysIn={DaysIn:F0}, nextSalary={NextSalary:yyyy-MM-dd} ({DaysUntil:F0} days away)",
            periodStart, daysIntoPeriod, nextExpectedSalary, daysUntilNextSalary);

        var spendThisPeriod = thisPeriodExpenses.Sum(t => t.Amount);
        var spendLastPeriodFull = lastPeriodFullExpenses.Sum(t => t.Amount);
        var spendLastPeriodPtd = lastPeriodPtdExpenses.Sum(t => t.Amount);
        
        // Year-over-Year (YoY) Seasonality Analysis
        var lastYearStart = periodStart.AddYears(-1);
        var lastYearToday = today.AddYears(-1);
        var lastYearExpensesPtd = expenses.Where(t => ToDate(t.TransactionDate) >= lastYearStart && ToDate(t.TransactionDate) <= lastYearToday).ToList();
        var spendLastYearPtd = lastYearExpensesPtd.Sum(t => t.Amount);
        decimal yoyChangePercent = spendLastYearPtd > 0 ? ((spendThisPeriod - spendLastYearPtd) / spendLastYearPtd) * 100 : 0;

        // Improved Recurring Expense Detection (Automated)
        var priorExpenses = expenses.Where(t => ToDate(t.TransactionDate) < periodStart).ToList();
        var analysisWindowDays = settings.AnalysisWindowDays > 0 ? settings.AnalysisWindowDays : 90;
        var recentCutoff = today.AddDays(-analysisWindowDays);

        var recurringNames = priorExpenses
            .GroupBy(t => NormalizeDescription(t.Description))
            .Select(g => {
                var count = g.Count();
                var recentCount = g.Count(t => ToDate(t.TransactionDate) >= recentCutoff);
                var avg = g.Average(t => t.Amount);
                var variance = count > 1 ? g.Sum(t => (t.Amount - avg) * (t.Amount - avg)) / (count - 1) : 0m;
                var stdDev = (decimal)Math.Sqrt((double)variance);
                var cv = avg > 0 ? stdDev / avg : 0m;
                var monthCount = g.GroupBy(t => $"{t.TransactionDate.Year}-{t.TransactionDate.Month}").Count();
                var avgFreq = monthCount > 0 ? (decimal)count / monthCount : 0m;
                
                // Track if any transaction in this group was a definitive debit order or EFT
                // Investec uses 'DEBIT' category for all card swipes, so we must check the original Description for SA banking keywords
                var isDebitOrEft = g.Any(t => 
                    (t.Description != null && (
                        t.Description.Contains("DEBIT ORDER", StringComparison.OrdinalIgnoreCase) ||
                        t.Description.Contains("MAGTAPE", StringComparison.OrdinalIgnoreCase) ||
                        t.Description.Contains("MAG TAPE", StringComparison.OrdinalIgnoreCase) ||
                        t.Description.Contains("NAEDO", StringComparison.OrdinalIgnoreCase) ||
                        t.Description.Contains("EFT", StringComparison.OrdinalIgnoreCase) ||
                        t.Description.Contains("STOP ORDER", StringComparison.OrdinalIgnoreCase) ||
                        t.Description.Contains("PAYMENT", StringComparison.OrdinalIgnoreCase)
                    )) ||
                    string.Equals(t.Category, "FASTER_PAY", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(t.Category, "TRANSFER", StringComparison.OrdinalIgnoreCase));

                return new { Name = g.Key, MonthCount = monthCount, CV = cv, AvgFreq = avgFreq, Count = count, RecentCount = recentCount, IsDebitOrEft = isDebitOrEft };
            })
            .Where(x => IsFixedCost(x.Name, settings) || (x.IsDebitOrEft && x.RecentCount > 2))
            .Select(x => x.Name)
            .ToHashSet();

        bool IsFixed(Transaction t) => recurringNames.Contains(NormalizeDescription(t.Description));

        // Pulse Comparison: If PTD spend is suspiciously low (< 10% of full), use Full month as baseline to avoid "700% increase" errors
        var pulseBaseline = (spendLastPeriodPtd < (spendLastPeriodFull * settings.PulseBaselineThreshold)) ? spendLastPeriodFull : spendLastPeriodPtd;

        var topCategories = thisPeriodExpenses
            .Where(t => !IsFixed(t))
            .GroupBy(t => t.Category ?? NormalizeDescription(t.Description))
            .Select(g => new { Name = g.Key, Amount = g.Sum(t => t.Amount) })
            .OrderByDescending(x => x.Amount).Take(5).ToList();

        var categoryReport = new List<CategorySpend>();
        foreach (var cat in topCategories)
        {
            var ptdLastSpend = lastPeriodPtdExpenses.Where(t => (t.Category ?? NormalizeDescription(t.Description)) == cat.Name).Sum(t => t.Amount);
            var fullLastSpend = lastPeriodFullExpenses.Where(t => (t.Category ?? NormalizeDescription(t.Description)) == cat.Name).Sum(t => t.Amount);
            
            // Hybrid Baseline: If it's a significant amount and PTD is zero, use full month to avoid spike hallucinations
            decimal baseline = (ptdLastSpend < (fullLastSpend * settings.HybridBaselineThreshold)) ? fullLastSpend : ptdLastSpend;
            
            decimal diff = cat.Amount - baseline;
            decimal percent = baseline > 0 ? (diff / baseline) * 100 : 100;

            bool isStable = Math.Abs(percent) < settings.StabilityPercentageThreshold || Math.Abs(diff) < settings.StabilityAmountThreshold;
            categoryReport.Add(new CategorySpend(cat.Name, cat.Amount, diff, percent, isStable, IsFixed(new Transaction { Description = cat.Name })));                                                                                                           
        }

        // Identify Upcoming Expected Payments
        var upcomingFixedCosts = new List<UpcomingExpense>();
        var historicalFixedExpenses = priorExpenses.Where(t => IsFixed(t))
            .GroupBy(t => NormalizeDescription(t.Description)).ToList();

        foreach (var group in historicalFixedExpenses)
        {
            if (!thisPeriodExpenses.Any(t => IsFixed(t) && NormalizeDescription(t.Description) == group.Key))
            {
                // Take the average of the last few occurrences to be more accurate than just the absolute sum of all history
                var avgAmount = group.OrderByDescending(x => x.TransactionDate).Take(3).Average(t => Math.Abs(t.Amount));
                upcomingFixedCosts.Add(new UpcomingExpense(group.Key, avgAmount));    
            }
        }
        decimal upcomingOverhead = upcomingFixedCosts.Sum(e => e.ExpectedAmount);
        _logger.LogInformation("[Actuarial] Upcoming fixed costs: {Count} items totalling R{Total:F2}. Items: {Names}",
            upcomingFixedCosts.Count, upcomingOverhead,
            upcomingFixedCosts.Any() ? string.Join(", ", upcomingFixedCosts.Select(c => $"{c.Name} (R{c.ExpectedAmount:F2})")) : "none");

        // ACTUARIAL REFINEMENT: Calculate baseBurn ONLY from variable expenses to prevent double-counting fixed costs
        var variableExpenses = expenses.Where(t => !IsFixed(t)).ToList();
        
        var windowStartDate = today.AddDays(-analysisWindowDays);
        var validVariableExpenses = variableExpenses.Where(t => ToDate(t.TransactionDate) >= windowStartDate).OrderBy(t => t.TransactionDate).ToList();
        var dailyVariableExpensesMap = validVariableExpenses.GroupBy(t => ToDate(t.TransactionDate)).ToDictionary(g => g.Key, g => (double)g.Sum(t => t.Amount));

        decimal alpha = settings.ActuarialAlpha > 0 ? settings.ActuarialAlpha : 0.15m;
        double weightedMean = 0; double weightedVar = 0; bool initialized = false;
        for (int i = 0; i < analysisWindowDays; i++)
        {
            var loopDate = windowStartDate.AddDays(i);
            double dailySpend = dailyVariableExpensesMap.TryGetValue(loopDate, out var value) ? value : 0.0;
            if (!initialized) { weightedMean = dailySpend; initialized = true; }
            else { double delta = dailySpend - weightedMean; weightedMean += (double)alpha * delta; weightedVar = (1 - (double)alpha) * (weightedVar + (double)alpha * Math.Pow(delta, 2)); }
        }

        var stdDev = Math.Sqrt(weightedVar);
        var baseBurn = (decimal)weightedMean > 0 ? (decimal)weightedMean : 1m;
        
        // PROJECTED SPEND: Separate linear variable projection + actual/expected fixed costs
        var variableSpendThisPeriod = thisPeriodExpenses.Where(t => !IsFixed(t)).Sum(t => t.Amount);
        var fixedSpendThisPeriod = thisPeriodExpenses.Where(t => IsFixed(t)).Sum(t => t.Amount);
        
        decimal projectedVariableSpend = (variableSpendThisPeriod / (decimal)daysIntoPeriod) * (decimal)avgCycleDays;
        decimal projectedMonthEndSpend = projectedVariableSpend + fixedSpendThisPeriod + upcomingOverhead;
        decimal projectedBalance = currentBalance - upcomingOverhead - (baseBurn * (decimal)daysUntilNextSalary);
        
        // 6. SOLVENCY PROBABILITY (Risk Modeling)
        // We use Student's t-distribution for "Black Swan" fat tails to model extreme risk events.
        // Probability that balance stays > 0 until next payday.
        double probSurvival = 0;
        if (stdDev > 0)
        {
            double totalRiskWindow = Math.Max(daysUntilNextSalary, 1.0);
            double df = settings.ActuarialDegreesOfFreedom;
            // t-statistic = (Projected Surplus) / (Volatility * sqrt(Time))
            double tStatistic = (double)(projectedBalance / (decimal)(stdDev * Math.Sqrt(totalRiskWindow)));
            probSurvival = StudentTCDF(tStatistic, df) * 100.0;
        }
        else { probSurvival = projectedBalance > 0 ? 100 : 0; }

        var dailyAvgSimple = validVariableExpenses.Any() ? (double)validVariableExpenses.Sum(t => t.Amount) / analysisWindowDays : 0;

        var trendMultiplierUpper = 1.0m + settings.TrendSensitivity;
        var trendMultiplierLower = 1.0m - settings.TrendSensitivity;
        string trendDirection = (decimal)weightedMean > (decimal)dailyAvgSimple * trendMultiplierUpper ? "Increasing" : ((decimal)weightedMean < (decimal)dailyAvgSimple * trendMultiplierLower ? "Decreasing" : "Stable");

        var lastSalaryAmount = salaryPayments.Any() ? Math.Abs(salaryPayments[0].Amount) : 0m;

        // ACTUARIAL REFINEMENT: Calculate Total Daily Burn by combining variable baseBurn and amortized fixed costs
        decimal totalMonthlyFixed = historicalFixedExpenses.Sum(g => g.OrderByDescending(t => t.TransactionDate).Take(3).Average(t => t.Amount));
        decimal dailyFixedBurn = totalMonthlyFixed / 30m;
        decimal trueDailyBurn = baseBurn + dailyFixedBurn;
        if (trueDailyBurn < 1m) trueDailyBurn = 1m;

        var expectedRunway = (currentBalance - upcomingOverhead) / trueDailyBurn;
        var safeRunway = (currentBalance - upcomingOverhead) / (decimal)((double)trueDailyBurn + stdDev);
        _logger.LogInformation(
            "[Actuarial] Burn: base={BaseBurn:F2}/day, fixed={FixedBurn:F2}/day, total={TotalBurn:F2}/day | Runway: expected={ExpectedRunway:F1}d, safe={SafeRunway:F1}d | Survival: {SurvivalProb:F1}% | Trend: {Trend}",
            baseBurn, dailyFixedBurn, trueDailyBurn, expectedRunway, safeRunway, probSurvival, trendDirection);
        _logger.LogInformation(
            "[Actuarial] Projections: spendThisPeriod=R{SpendThis:F2}, projectedCycleSpend=R{ProjSpend:F2}, projectedPaydayBalance=R{PaydayBal:F2}",
            spendThisPeriod, projectedMonthEndSpend, projectedBalance);

        return await Task.FromResult(new FinancialHealthReport(
            CurrentBalance: currentBalance,
            WeightedDailyBurn: trueDailyBurn,
            MonthlyBurnRate: trueDailyBurn * 30,
            BurnVolatility: stdDev,
            SafeRunwayDays: (currentBalance - upcomingOverhead) / (decimal)((double)trueDailyBurn + stdDev),
            ExpectedRunwayDays: (currentBalance - upcomingOverhead) / trueDailyBurn,
            OptimisticRunwayDays: (currentBalance - upcomingOverhead) / (decimal)Math.Max((double)trueDailyBurn - stdDev, 1.0),
            ValueAtRisk95: (decimal)(weightedMean + (settings.VarConfidenceInterval * stdDev)),
            TrendDirection: trendDirection,
            SpendThisMonth: spendThisPeriod,
            SpendLastMonth: pulseBaseline,
            SpendSameMonthLastYear: spendLastYearPtd,
            YoYChangePercentage: yoyChangePercent,
            ProjectedMonthEndSpend: projectedMonthEndSpend,
            ProjectedBalanceAtNextSalary: projectedBalance,
            UpcomingExpectedPayments: upcomingOverhead,
            LastDetectedSalaryAmount: lastSalaryAmount,
            DaysUntilNextSalary: (int)Math.Round(daysUntilNextSalary),
            RunwayProbability: Math.Min(Math.Max(probSurvival, 0), 100),
            TopCategories: categoryReport.OrderByDescending(c => c.Amount).Take(3).ToList(),
            UpcomingFixedCosts: upcomingFixedCosts
        ));
    }

    private double NormalCDF(double x)
    {
        double a1 = 0.254829592; double a2 = -0.284496736; double a3 = 1.421413741; double a4 = -1.453152027; double a5 = 1.061405429; double p = 0.3275911;
        int sign = 1; if (x < 0) sign = -1;
        x = Math.Abs(x) / Math.Sqrt(2.0);
        double t = 1.0 / (1.0 + p * x);
        double y = 1.0 - (((((a5 * t + a4) * t) + a3) * t + a2) * t + a1) * t * Math.Exp(-x * x);
        return 0.5 * (1.0 + sign * y);
    }

    /// <summary>
    /// Student's t-distribution CDF approximation (Bailey's method)
    /// Provides fat-tailed risk modeling for Black Swan events.
    /// </summary>
    private double StudentTCDF(double t, double df)
    {
        // Bailey's approximation: transform t to a normal z-score
        // Highly accurate for df > 1
        double z = t * (1.0 - 1.0 / (4.0 * df)) / Math.Sqrt(1.0 + t * t / (2.0 * df));
        return NormalCDF(z);
    }
}
