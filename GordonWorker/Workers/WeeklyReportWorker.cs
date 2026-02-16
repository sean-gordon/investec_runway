using Dapper;
using GordonWorker.Services;
using Npgsql;

namespace GordonWorker.Workers;

public class WeeklyReportWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<WeeklyReportWorker> _logger;
    private readonly IConfiguration _configuration;
    private readonly Dictionary<int, DateTime> _lastSentPerUser = new();

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
                var userIds = await connection.QueryAsync<int>("SELECT id FROM users");

                var now = DateTime.Now;

                foreach (var userId in userIds)
                {
                    var settings = await settingsService.GetSettingsAsync(userId);
                    
                    if (Enum.TryParse<DayOfWeek>(settings.ReportDayOfWeek, true, out var targetDay) &&
                        now.DayOfWeek == targetDay && 
                        now.Hour == settings.ReportHour)
                    {
                        _lastSentPerUser.TryGetValue(userId, out var lastSent);
                        if (lastSent.Date != now.Date)
                        {
                            try
                            {
                                await reportService.GenerateAndSendReportAsync(userId);
                                _lastSentPerUser[userId] = now;
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Failed to send weekly report for user {UserId}", userId);
                            }
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
