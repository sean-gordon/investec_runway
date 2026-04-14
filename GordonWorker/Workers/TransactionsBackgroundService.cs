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
            var configuration = userScope.ServiceProvider.GetRequiredService<IConfiguration>();

            // Acquire a non-blocking Postgres advisory lock keyed to this user.
            // If another instance (or a previous slow sync cycle) already holds the lock,
            // we skip rather than pile up — the next 60-second tick will try again.
            await using var lockConn = new NpgsqlConnection(configuration.GetConnectionString("DefaultConnection"));
            await lockConn.OpenAsync(token);

            var lockKey = (long)HashCode.Combine("txsync", userId);
            var acquired = await lockConn.ExecuteScalarAsync<bool>(
                "SELECT pg_try_advisory_lock(@Key)", new { Key = lockKey });

            if (!acquired)
            {
                _logger.LogDebug("Skipping sync for user {UserId} — advisory lock held by another instance.", userId);
                return;
            }

            try
            {
                var syncService = userScope.ServiceProvider.GetRequiredService<ITransactionSyncService>();
                await syncService.SyncTransactionsAsync(userId, token: token);
            }
            finally
            {
                await lockConn.ExecuteAsync("SELECT pg_advisory_unlock(@Key)", new { Key = lockKey });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error syncing transactions for user {UserId}", userId);
        }
    }
}
