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
                // Fetch users first
                IEnumerable<UserReportStatus> users;
                using (var scope = _serviceProvider.CreateScope())
                {
                    var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
                    using var connection = new NpgsqlConnection(configuration.GetConnectionString("DefaultConnection"));
                    users = await connection.QueryAsync<UserReportStatus>("SELECT id, last_weekly_report_sent as LastWeeklyReportSent FROM users");
                }

                var now = DateTime.Now;

                var tasks = users.Select(user => ProcessUserReportAsync(user, now, stoppingToken));
                await Task.WhenAll(tasks);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Weekly Report Worker.");
            }

            // Check every 5 mins
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }

    private async Task ProcessUserReportAsync(UserReportStatus user, DateTime now, CancellationToken token)
    {
        try
        {
            // Optimization: Skip immediately if already sent today
            if (user.LastWeeklyReportSent.HasValue && user.LastWeeklyReportSent.Value.Date == now.Date) return;

            // Create per-user scope
            using var userScope = _serviceProvider.CreateScope();
            var settingsService = userScope.ServiceProvider.GetRequiredService<ISettingsService>();
            var reportService = userScope.ServiceProvider.GetRequiredService<IFinancialReportService>();
            var config = userScope.ServiceProvider.GetRequiredService<IConfiguration>();

            var settings = await settingsService.GetSettingsAsync(user.Id);

            if (Enum.TryParse<DayOfWeek>(settings.ReportDayOfWeek, true, out var targetDay) &&
                now.DayOfWeek == targetDay &&
                now.Hour == settings.ReportHour)
            {
                await reportService.GenerateAndSendReportAsync(user.Id);

                // Update DB immediately to prevent double-send
                using var updateConnection = new NpgsqlConnection(config.GetConnectionString("DefaultConnection"));
                await updateConnection.ExecuteAsync("UPDATE users SET last_weekly_report_sent = @Now WHERE id = @Id", new { Now = now, user.Id });
                _logger.LogInformation("Weekly report sent for user {UserId}", user.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process weekly report for user {UserId}", user.Id);
        }
    }
}

public record UserReportStatus(int Id, DateTime? LastWeeklyReportSent);
