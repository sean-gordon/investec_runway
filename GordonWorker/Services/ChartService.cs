using ScottPlot;

namespace GordonWorker.Services;

public interface IChartService
{
    byte[] GenerateRunwayChart(List<Models.Transaction> history, decimal currentBalance, double averageDailyBurn);
    byte[] GenerateGenericChart(string title, string type, List<(string Label, double Value)> data);
}

public class ChartService : IChartService
{
    public byte[] GenerateGenericChart(string title, string type, List<(string Label, double Value)> data)
    {
        var plt = new Plot();
        try { ScottPlot.Fonts.Default = "DejaVu Sans"; } catch { }

        plt.Title(title);
        plt.FigureBackground.Color = Colors.White;
        plt.DataBackground.Color = Colors.White;
        plt.Axes.Color(Colors.Black);
        plt.Grid.IsVisible = true;
        plt.Grid.MajorLineColor = Colors.Black.WithOpacity(0.1);

        if (type.ToLower() == "bar")
        {
            var values = data.Select(d => d.Value).ToArray();
            var positions = Enumerable.Range(0, data.Count).Select(i => (double)i).ToArray();
            
            var bars = plt.Add.Bars(values);
            
            // Set X-axis ticks to labels
            var ticks = data.Select((d, i) => new Tick((double)i, d.Label)).ToArray();
            plt.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericManual(ticks);
            plt.Axes.Bottom.TickLabelStyle.Rotation = 45;
            plt.Axes.Bottom.TickLabelStyle.Alignment = Alignment.UpperLeft;
        }
        else // Default to line/scatter
        {
            var values = data.Select(d => d.Value).ToArray();
            // Try to parse labels as dates for better formatting if possible
            if (DateTime.TryParse(data.FirstOrDefault().Label, out _))
            {
                var dates = data.Select(d => DateTime.Parse(d.Label)).ToArray();
                var sig = plt.Add.Scatter(dates, values);
                plt.Axes.DateTimeTicksBottom();
            }
            else
            {
                var positions = Enumerable.Range(0, data.Count).Select(i => (double)i).ToArray();
                plt.Add.Scatter(positions, values);
                var ticks = data.Select((d, i) => new Tick((double)i, d.Label)).ToArray();
                plt.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericManual(ticks);
            }
        }

        plt.Axes.AutoScale();
        return plt.GetImageBytes(1000, 600, ImageFormat.Png);
    }

    public byte[] GenerateRunwayChart(List<Models.Transaction> history, decimal currentBalance, double averageDailyBurn)
    {
        var plt = new Plot();
        
        // Setup font for Linux/Docker environments
        try { ScottPlot.Fonts.Default = "DejaVu Sans"; } catch { }

        // 1. Data Preparation
        var now = DateTime.UtcNow;
        var cutoff = DateTimeOffset.UtcNow.AddDays(-30);
        
        // Sort history NEWEST to OLDEST for reconstruction
        var sortedHistory = history
            .Where(t => t.TransactionDate >= cutoff)
            .OrderByDescending(t => t.TransactionDate)
            .ToList();

        var plotPoints = new List<(DateTime Date, double Balance)>();

        // Reconstruct historical balances
        double runner = (double)currentBalance;
        
        // Add "Now" point
        plotPoints.Add((now, runner));

        foreach (var tx in sortedHistory)
        {
            // Reverse transactions to go back in time
            // Previous Balance = Current - (+Income) or Current - (-Expense)
            runner -= (double)tx.Amount; 
            plotPoints.Add((tx.TransactionDate.UtcDateTime, runner));
        }

        // Sort chronologically for plotting (Oldest -> Newest)
        var finalPoints = plotPoints.OrderBy(p => p.Date).ToList();
        var histDates = finalPoints.Select(p => p.Date).ToArray();
        var histBalances = finalPoints.Select(p => p.Balance).ToArray();

        // 2. Projection
        var projDates = new List<DateTime>();
        var projBalances = new List<double>();

        projDates.Add(now);
        projBalances.Add((double)currentBalance);

        double projRunner = (double)currentBalance;
        var projDate = now;
        
        // Project 30 days
        for (int i = 1; i <= 30; i++)
        {
            projDate = projDate.AddDays(1);
            projRunner -= averageDailyBurn;
            projDates.Add(projDate);
            projBalances.Add(projRunner);
            if (projRunner <= -5000) break;
        }

        // 3. Plotting
        if (histDates.Length > 1)
        {
            var hist = plt.Add.Scatter(histDates, histBalances);
            hist.Color = Colors.Blue;
            hist.LineWidth = 3;
            hist.MarkerSize = 0; 
            hist.LegendText = "History";
        }
        else
        {
            // Add a watermark if no data
            var txt = plt.Add.Text("NO TRANSACTION DATA FOUND", 0, 0);
            txt.LabelFontSize = 24;
            txt.LabelBold = true;
            txt.LabelFontColor = Colors.Red.WithOpacity(0.3);
            txt.LabelAlignment = Alignment.MiddleCenter;
        }

        var proj = plt.Add.Scatter(projDates.ToArray(), projBalances.ToArray());
        proj.Color = Colors.Red;
        proj.LineWidth = 2;
        proj.LinePattern = LinePattern.Dashed;
        proj.MarkerSize = 0;
        proj.LegendText = $"Burn (R{averageDailyBurn:N0}/day)";

        // Formatting
        plt.Title("Financial Outlook & Projection");
        plt.YLabel("Balance (ZAR)");
        plt.XLabel("Date");
        
        plt.Axes.DateTimeTicksBottom();
        
        plt.FigureBackground.Color = Colors.White;
        plt.DataBackground.Color = Colors.White;
        plt.Axes.Color(Colors.Black);
        
        plt.Grid.MajorLineColor = Colors.Black.WithOpacity(0.15);
        plt.Grid.IsVisible = true;
        
        var legend = plt.ShowLegend(Edge.Right);
        
        var zeroLine = plt.Add.HorizontalLine(0);
        zeroLine.Color = Colors.Gray;
        zeroLine.LineWidth = 1;
        zeroLine.LinePattern = LinePattern.Dotted;

        plt.Axes.AutoScale();

        return plt.GetImageBytes(1000, 600, ImageFormat.Png);
    }
}
