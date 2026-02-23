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

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
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

            // Find the System Admin user for global status reporting
            int? adminUserId = null;
            using (var connection = new Npgsql.NpgsqlConnection(configuration.GetConnectionString("DefaultConnection")))
            {
                adminUserId = await Dapper.SqlMapper.QueryFirstOrDefaultAsync<int?>(connection, "SELECT id FROM users WHERE is_system = TRUE LIMIT 1");
            }

            // Get all users for warming
            var usersToCheck = new List<int>();
            using (var connection = new Npgsql.NpgsqlConnection(configuration.GetConnectionString("DefaultConnection")))
            {
                usersToCheck = (await Dapper.SqlMapper.QueryAsync<int>(connection, "SELECT user_id FROM user_settings")).ToList();
            }

            if (!usersToCheck.Any())
            {
                _logger.LogInformation("Connectivity check skipped: No users found.");
                return;
            }

            _logger.LogInformation("Checking connectivity for {Count} users. Global status reported for Admin ID: {AdminId}", usersToCheck.Count, adminUserId);

            foreach (var userId in usersToCheck)
            {
                try
                {
                    var settings = await settingsService.GetSettingsAsync(userId);
                    
                    // 2. Check Investec
                    if (!string.IsNullOrEmpty(settings.InvestecClientId))
                    {
                        client.Configure(settings.InvestecClientId, settings.InvestecSecret, settings.InvestecApiKey);
                        var (isOnline, error) = await client.TestConnectivityAsync();
                        
                        // Global status reported for Admin
                        if (userId == adminUserId)
                        {
                            _statusService.IsInvestecOnline = isOnline;
                            _statusService.LastInvestecCheck = DateTime.UtcNow;
                        }
                        if (!isOnline) _logger.LogWarning("Investec API is OFFLINE for user {UserId}. Error: {Error}", userId, error);
                    }

                    // 3. Check AI Providers
                    var (primaryOk, _) = await aiService.TestConnectionAsync(userId, useFallback: false);
                    
                    if (userId == adminUserId)
                        _statusService.IsAiPrimaryOnline = primaryOk;

                    if (settings.EnableAiFallback)
                    {
                        var (fallbackOk, _) = await aiService.TestConnectionAsync(userId, useFallback: true);
                        if (userId == adminUserId)
                            _statusService.IsAiFallbackOnline = fallbackOk;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed connectivity check for user {UserId}", userId);
                }
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
