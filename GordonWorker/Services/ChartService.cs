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
        // We want to show the last 30 days of balance history + 30 days of projection
        var relevantHistory = history
            .Where(t => t.TransactionDate >= DateTimeOffset.UtcNow.AddDays(-30))
            .OrderBy(t => t.TransactionDate)
            .ToList();

        var dates = new List<DateTime>();
        var balances = new List<double>();

        // Reconstruct historical balances (running total backwards from current)
        // Note: history contains 'balance' field from bank, use that if available and reliable, 
        // but bank balance snapshots might be sparse. Best to plot the snapshots we have.
        foreach (var tx in relevantHistory)
        {
            if (tx.Balance != 0) // Only use points where balance was captured
            {
                dates.Add(tx.TransactionDate.UtcDateTime);
                balances.Add((double)tx.Balance);
            }
        }

        // Add current point
        dates.Add(DateTime.UtcNow);
        balances.Add((double)currentBalance);

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
        plt.YLabel("Balance (ZAR)");
        plt.XLabel("Date");
        plt.Axes.DateTimeTicksBottom();
        
        // Add a horizontal line at 0
        var zeroLine = plt.Add.HorizontalLine(0);
        zeroLine.Color = Colors.Black.WithOpacity(0.5);
        zeroLine.LinePattern = LinePattern.Dotted;

        // 4. Render
        return plt.GetImageBytes(600, 400, ImageFormat.Png);
    }
}
