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
    private readonly ISettingsService _settingsService;
    private DateTime _lastSent = DateTime.MinValue;

    public WeeklyReportWorker(IServiceProvider serviceProvider, ILogger<WeeklyReportWorker> logger, ISettingsService settingsService)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
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
                    using var scope = _serviceProvider.CreateScope();
                    var reportService = scope.ServiceProvider.GetRequiredService<IFinancialReportService>();
                    await reportService.GenerateAndSendReportAsync();
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
}
