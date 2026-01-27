using Dapper;
using GordonWorker.Models;
using GordonWorker.Services;
using Npgsql;
using System.Text.Json;

namespace GordonWorker.Workers;

public class WeeklyReportWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<WeeklyReportWorker> _logger;
    private readonly IConfiguration _configuration;
    private readonly ISettingsService _settingsService;
    private DateTime _lastSent = DateTime.MinValue;

    public WeeklyReportWorker(IServiceProvider serviceProvider, ILogger<WeeklyReportWorker> logger, IConfiguration configuration, ISettingsService settingsService)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _configuration = configuration;
        _settingsService = settingsService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Weekly Report Worker starting.");

        while (!stoppingToken.IsCancellationRequested)
        {
            var settings = await _settingsService.GetSettingsAsync();
            var now = DateTime.Now;
            
            if (Enum.TryParse<DayOfWeek>(settings.ReportDayOfWeek, true, out var targetDay) &&
                now.DayOfWeek == targetDay && 
                now.Hour == settings.ReportHour && 
                _lastSent.Date != now.Date)
            {
                try
                {
                    await GenerateAndSendReportAsync();
                    _lastSent = now;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to send weekly report.");
                }
            }

            // Check every hour
            await Task.Delay(TimeSpan.FromMinutes(60), stoppingToken);
        }
    }

    private async Task GenerateAndSendReportAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();
        var actuary = scope.ServiceProvider.GetRequiredService<IActuarialService>();
        var ollama = scope.ServiceProvider.GetRequiredService<IOllamaService>();
        var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();

        using var connection = new NpgsqlConnection(config.GetConnectionString("DefaultConnection"));
        await connection.OpenAsync();

        // 1. Get Data (Last 14 days for comparison)
        var sql = "SELECT * FROM transactions WHERE transaction_date >= NOW() - INTERVAL '14 days'";
        var transactions = (await connection.QueryAsync<Transaction>(sql)).ToList();

        var thisWeek = transactions.Where(t => t.TransactionDate >= DateTimeOffset.UtcNow.AddDays(-7)).ToList();
        var lastWeek = transactions.Where(t => t.TransactionDate < DateTimeOffset.UtcNow.AddDays(-7)).ToList();

        // 2. Get Current Balance
        var sqlBalance = "SELECT balance FROM transactions ORDER BY transaction_date DESC LIMIT 1";
        var currentBalance = await connection.ExecuteScalarAsync<decimal?>(sqlBalance) ?? 0;

        // 3. Actuarial Analysis
        // We use full history for the main health report, but for this week's comparison we just sum up
        var fullHistorySql = "SELECT * FROM transactions WHERE transaction_date >= NOW() - INTERVAL '90 days'";
        var fullHistory = (await connection.QueryAsync<Transaction>(fullHistorySql)).ToList();
        var healthReport = actuary.AnalyzeHealth(fullHistory, currentBalance);

        // 4. Prepare Comparison Stats
        var thisWeekSpend = thisWeek.Where(t => t.Amount < 0).Sum(t => t.Amount);
        var lastWeekSpend = lastWeek.Where(t => t.Amount < 0).Sum(t => t.Amount);

        var stats = new
        {
            CurrentBalance = currentBalance,
            SpendThisWeek = Math.Abs(thisWeekSpend),
            SpendLastWeek = Math.Abs(lastWeekSpend),
            RunwayDays = healthReport.ExpectedRunwayDays,
            Volatility = healthReport.BurnVolatility,
            Trend = healthReport.TrendDirection
        };

        var jsonStats = JsonSerializer.Serialize(stats);

        // 5. Generate Content with AI
        var aiExplanation = await ollama.GenerateSimpleReportAsync(jsonStats);

        // 6. Send Email
        var subject = $"Weekly Financial Report - {DateTime.Now:dd MMM yyyy}";
        var body = $@"
            <h1>Weekly Finance Update</h1>
            {aiExplanation}
            <hr>
            <h3>Raw Stats</h3>
            <ul>
                <li>Current Balance: {currentBalance:C}</li>
                <li>Spend This Week: {Math.Abs(thisWeekSpend):C}</li>
                <li>Spend Last Week: {Math.Abs(lastWeekSpend):C}</li>
                <li>Estimated Runway: {healthReport.ExpectedRunwayDays:F0} days</li>
            </ul>
        ";

        await emailService.SendEmailAsync(subject, body);
    }
}
