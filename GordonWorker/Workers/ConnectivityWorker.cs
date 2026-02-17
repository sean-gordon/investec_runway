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

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await CheckConnectivityAsync();
        }
    }

    private async Task CheckConnectivityAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<IInvestecClient>();
        var aiService = scope.ServiceProvider.GetRequiredService<IAiService>();
        var settingsService = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();

        try
        {
            // 1. Check Database
            try
            {
                using var connection = new Npgsql.NpgsqlConnection(configuration.GetConnectionString("DefaultConnection"));
                await connection.OpenAsync();
                _statusService.IsDatabaseOnline = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Database connectivity check failed.");
                _statusService.IsDatabaseOnline = false;
                return; // Can't proceed without DB
            }

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
            
            // 2. Check Investec
            if (!string.IsNullOrEmpty(adminSettings.InvestecClientId))
            {
                client.Configure(adminSettings.InvestecClientId, adminSettings.InvestecSecret, adminSettings.InvestecApiKey);
                var (isOnline, error) = await client.TestConnectivityAsync();
                _statusService.IsInvestecOnline = isOnline;
                _statusService.LastInvestecCheck = DateTime.UtcNow;
                if (!isOnline) _logger.LogWarning("Investec API is OFFLINE. Error: {Error}", error);
            }

            // 3. Check AI Providers
            var (primaryOk, _) = await aiService.TestConnectionAsync(adminUserId.Value, useFallback: false);
            _statusService.IsAiPrimaryOnline = primaryOk;

            if (adminSettings.EnableAiFallback)
            {
                var (fallbackOk, _) = await aiService.TestConnectionAsync(adminUserId.Value, useFallback: true);
                _statusService.IsAiFallbackOnline = fallbackOk;
            }
            else
            {
                _statusService.IsAiFallbackOnline = false;
            }
            
            _statusService.LastAiCheck = DateTime.UtcNow;

            _logger.LogInformation("Connectivity check complete. DB: {Db}, Investec: {Inv}, AI Primary: {AiP}, AI Fallback: {AiF}",
                _statusService.IsDatabaseOnline, _statusService.IsInvestecOnline, _statusService.IsAiPrimaryOnline, _statusService.IsAiFallbackOnline);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred during connectivity check.");
        }
    }
}
