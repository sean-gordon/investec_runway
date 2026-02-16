using ScottPlot;

namespace GordonWorker.Services;

public interface IChartService
{
    byte[] GenerateRunwayChart(List<Models.Transaction> history, decimal currentBalance, double averageDailyBurn);
}

public class ChartService : IChartService
{
    public byte[] GenerateRunwayChart(List<Models.Transaction> history, decimal currentBalance, double averageDailyBurn)
    {
        var plt = new Plot();
        
        // 1. Data Preparation
        // We want to show the last 30 days of balance history
        var relevantHistory = history
            .Where(t => t.TransactionDate >= DateTimeOffset.UtcNow.AddDays(-30))
            .OrderByDescending(t => t.TransactionDate) // newest first for walking back
            .ToList();

        var dates = new List<DateTime>();
        var balances = new List<double>();

        // Reconstruct historical balances (running total backwards from current)
        // Start at current
        double runner = (double)currentBalance;
        dates.Add(DateTime.UtcNow);
        balances.Add(runner);

        foreach (var tx in relevantHistory)
        {
            // Reverse the transaction effect to find previous balance
            // If tx.Amount was +100 (Expense), balance *before* was Current + 100? 
            // Wait: Balance After = Balance Before - Amount (if Amount is expense/positive).
            // So: Balance Before = Balance After + Amount.
            // Investec: Debits are positive. Credits are negative.
            // Balance = Previous - Debit. => Previous = Balance + Debit.
            
            runner += (double)tx.Amount; 
            
            dates.Add(tx.TransactionDate.UtcDateTime);
            balances.Add(runner);
        }

        // The lists are now Newest -> Oldest. Reverse them for plotting.
        dates.Reverse();
        balances.Reverse();

        // 2. Projection (Runway)
        var lastDate = DateTime.UtcNow;
        var lastBalance = (double)currentBalance;
        
        var projectedDates = new List<DateTime>();
        var projectedBalances = new List<double>();

        projectedDates.Add(lastDate);
        projectedBalances.Add(lastBalance);

        // Project until 0 or 60 days out
        for (int i = 1; i <= 60; i++)
        {
            lastDate = lastDate.AddDays(1);
            lastBalance -= averageDailyBurn;
            
            projectedDates.Add(lastDate);
            projectedBalances.Add(lastBalance);

            if (lastBalance <= 0) break;
        }

        // 3. Plotting
        // Historical Line (Blue)
        var histScatter = plt.Add.Scatter(dates.ToArray(), balances.ToArray());
        histScatter.Color = Colors.Blue;
        histScatter.LineWidth = 3;
        histScatter.LegendText = "History";

        // Projected Line (Red dashed)
        var projScatter = plt.Add.Scatter(projectedDates.ToArray(), projectedBalances.ToArray());
        projScatter.Color = Colors.Red;
        projScatter.LineWidth = 2;
        projScatter.LinePattern = LinePattern.Dashed;
        projScatter.LegendText = "Burn Projection";

        // Formatting
        plt.Title("Financial Runway");
        
        // Y-Axis Currency
        plt.YLabel("Balance (ZAR)");
        plt.Axes.Left.TickGenerator = new ScottPlot.TickGenerators.NumericAutomatic();
        plt.Axes.Left.Label.Text = "Balance (ZAR)";

        // Custom Y-axis formatter for currency
        // ScottPlot 5 uses TickGenerators. I'll stick to basic numeric labels if complex formatting is hard, 
        // but let's try to set the format string.
        
        plt.XLabel("Date");
        plt.Axes.DateTimeTicksBottom();
        
        // Style adjustments for visibility
        plt.FigureBackground.Color = Colors.White;
        plt.DataBackground.Color = Colors.White;
        
        // Axis line colors and tick colors
        plt.Axes.Left.FrameLineStyle.Color = Colors.Black;
        plt.Axes.Bottom.FrameLineStyle.Color = Colors.Black;
        
        // Add Grid
        plt.Grid.MajorLineColor = Colors.Black.WithOpacity(0.1);
        plt.Grid.IsVisible = true;

        // Add Legend
        plt.ShowLegend(Edge.Right);
        
        // Add a horizontal line at 0
        var zeroLine = plt.Add.HorizontalLine(0);
        zeroLine.Color = Colors.Black.WithOpacity(0.5);
        zeroLine.LinePattern = LinePattern.Dotted;

        // 4. Render
        return plt.GetImageBytes(1000, 600, ImageFormat.Png);
    }
}
