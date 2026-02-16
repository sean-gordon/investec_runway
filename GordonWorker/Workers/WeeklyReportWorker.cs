using Dapper;
using GordonWorker.Services;
using Npgsql;

namespace GordonWorker.Workers;

public class WeeklyReportWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<WeeklyReportWorker> _logger;
    private readonly IConfiguration _configuration;

    public WeeklyReportWorker(IServiceProvider serviceProvider, ILogger<WeeklyReportWorker> logger, IConfiguration configuration)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _configuration = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Multi-tenant Weekly Report Worker starting.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var settingsService = scope.ServiceProvider.GetRequiredService<ISettingsService>();
                var reportService = scope.ServiceProvider.GetRequiredService<IFinancialReportService>();

                using var connection = new NpgsqlConnection(_configuration.GetConnectionString("DefaultConnection"));
                var users = await connection.QueryAsync<UserReportStatus>("SELECT id, last_weekly_report_sent as LastWeeklyReportSent FROM users");

                var now = DateTime.Now;

                foreach (var user in users)
                {
                    // Optimization: Skip immediately if already sent today
                    if (user.LastWeeklyReportSent.HasValue && user.LastWeeklyReportSent.Value.Date == now.Date) continue;

                    var settings = await settingsService.GetSettingsAsync(user.Id);
                    
                    if (Enum.TryParse<DayOfWeek>(settings.ReportDayOfWeek, true, out var targetDay) &&
                        now.DayOfWeek == targetDay && 
                        now.Hour == settings.ReportHour)
                    {
                        try
                        {
                            await reportService.GenerateAndSendReportAsync(user.Id);
                            
                            // Update DB immediately to prevent double-send
                            await connection.ExecuteAsync("UPDATE users SET last_weekly_report_sent = @Now WHERE id = @Id", new { Now = now, user.Id });
                            _logger.LogInformation("Weekly report sent for user {UserId}", user.Id);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to send weekly report for user {UserId}", user.Id);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Weekly Report Worker.");
            }

            // Check every 5 mins
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }
}

public record UserReportStatus(int Id, DateTime? LastWeeklyReportSent);
