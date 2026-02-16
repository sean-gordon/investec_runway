using GordonWorker.Services;

namespace GordonWorker.Workers;

public class ConnectivityWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ConnectivityWorker> _logger;
    private readonly ISystemStatusService _statusService;

    public ConnectivityWorker(IServiceProvider serviceProvider, ILogger<ConnectivityWorker> logger, ISystemStatusService statusService)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _statusService = statusService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Connectivity Worker starting.");

        // Initial check on startup
        await CheckConnectivityAsync();

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await CheckConnectivityAsync();
        }
    }

    private async Task CheckConnectivityAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<IInvestecClient>();
        var settingsService = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();

        try
        {
            // Find the System Admin user to test global connectivity
            int? adminUserId = null;
            using (var connection = new Npgsql.NpgsqlConnection(configuration.GetConnectionString("DefaultConnection")))
            {
                adminUserId = await Dapper.SqlMapper.QueryFirstOrDefaultAsync<int?>(connection, "SELECT id FROM users WHERE is_system = TRUE LIMIT 1");
            }

            if (adminUserId == null)
            {
                _logger.LogInformation("Connectivity check skipped: No system admin user found.");
                return;
            }

            var adminSettings = await settingsService.GetSettingsAsync(adminUserId.Value);
            
            if (string.IsNullOrEmpty(adminSettings.InvestecClientId))
            {
                _logger.LogInformation("Connectivity check skipped: System admin has not configured Investec credentials.");
                return;
            }

            client.Configure(adminSettings.InvestecClientId, adminSettings.InvestecSecret, adminSettings.InvestecApiKey);
            var (isOnline, error) = await client.TestConnectivityAsync();
            
            _statusService.IsInvestecOnline = isOnline;
            _statusService.LastInvestecCheck = DateTime.UtcNow;
            _statusService.LastError = error;

            if (isOnline)
                _logger.LogInformation("Investec API is ONLINE.");
            else
                _logger.LogWarning("Investec API is OFFLINE. Error: {Error}", error);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred during connectivity check.");
        }
    }
}
