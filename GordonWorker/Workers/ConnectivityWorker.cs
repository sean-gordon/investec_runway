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

            // Find the best user for global status reporting (prefer the first admin with settings)
            int? statusUserId = null;
            using (var connection = new Npgsql.NpgsqlConnection(configuration.GetConnectionString("DefaultConnection")))
            {
                statusUserId = await Dapper.SqlMapper.QueryFirstOrDefaultAsync<int?>(connection, 
                    "SELECT u.id FROM users u JOIN user_settings s ON u.id = s.user_id WHERE u.role = 'Admin' ORDER BY u.is_system DESC, u.id ASC LIMIT 1");
                
                // Fallback to any system user if no admin settings found
                if (statusUserId == null)
                {
                    statusUserId = await Dapper.SqlMapper.QueryFirstOrDefaultAsync<int?>(connection, "SELECT id FROM users WHERE is_system = TRUE LIMIT 1");
                }
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

            _logger.LogInformation("Checking connectivity for {Count} users. Global status reported for User ID: {StatusUserId}", usersToCheck.Count, statusUserId);

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
                        
                        // Global status reported for our chosen status user
                        if (userId == statusUserId)
                        {
                            _statusService.IsInvestecOnline = isOnline;
                            _statusService.LastInvestecCheck = DateTime.UtcNow;
                        }
                        if (!isOnline) _logger.LogWarning("Investec API is OFFLINE for user {UserId}. Error: {Error}", userId, error);
                    }

                    // 3. Check AI Providers
                    // We only test ALL users if they use Ollama (to keep models warm). 
                    // For Gemini, we only test the status user to save quota.
                    bool shouldTestPrimary = (settings.AiProvider == "Ollama") || (userId == statusUserId);
                    bool shouldTestFallback = settings.EnableAiFallback && ((settings.FallbackAiProvider == "Ollama") || (userId == statusUserId));

                    if (shouldTestPrimary)
                    {
                        var (primaryOk, _) = await aiService.TestConnectionAsync(userId, useFallback: false);
                        if (userId == statusUserId)
                            _statusService.IsAiPrimaryOnline = primaryOk;
                    }

                    if (shouldTestFallback)
                    {
                        var (fallbackOk, _) = await aiService.TestConnectionAsync(userId, useFallback: true);
                        if (userId == statusUserId)
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
