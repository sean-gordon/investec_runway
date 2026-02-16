using Dapper;
using GordonWorker.Services;
using Npgsql;

namespace GordonWorker.Workers;

public class TransactionsBackgroundService : BackgroundService
{
    private readonly ILogger<TransactionsBackgroundService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly SemaphoreSlim _syncSemaphore = new(5); // Max 5 concurrent syncs

    public TransactionsBackgroundService(
        ILogger<TransactionsBackgroundService> logger,
        IServiceProvider serviceProvider,
        IConfiguration configuration)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _configuration = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Multi-tenant Transactions Background Service starting.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Get all users first
                IEnumerable<int> userIds;
                using (var scope = _serviceProvider.CreateScope())
                {
                    var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
                    using var connection = new NpgsqlConnection(configuration.GetConnectionString("DefaultConnection"));
                    userIds = await connection.QueryAsync<int>("SELECT id FROM users");
                }

                // Sync with rate limiting to prevent API throttling
                var tasks = userIds.Select(async userId =>
                {
                    await _syncSemaphore.WaitAsync(stoppingToken);
                    try
                    {
                        await SyncUserTransactionsAsync(userId, stoppingToken);
                    }
                    finally
                    {
                        _syncSemaphore.Release();
                    }
                });
                await Task.WhenAll(tasks);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred in Transactions Background Service.");
            }

            await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
        }
    }

    private async Task SyncUserTransactionsAsync(int userId, CancellationToken token)
    {
        try
        {
            // Create a NEW scope for each user to ensure fresh Scoped services (like InvestecClient)
            using var userScope = _serviceProvider.CreateScope();
            var syncService = userScope.ServiceProvider.GetRequiredService<ITransactionSyncService>();
            await syncService.SyncTransactionsAsync(userId, token: token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error syncing transactions for user {UserId}", userId);
        }
    }
}
