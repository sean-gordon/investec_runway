using Dapper;
using GordonWorker.Services;
using Npgsql;

namespace GordonWorker.Workers;

public class TransactionsBackgroundService : BackgroundService
{
    private readonly ILogger<TransactionsBackgroundService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;

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
                using var scope = _serviceProvider.CreateScope();
                var syncService = scope.ServiceProvider.GetRequiredService<ITransactionSyncService>();
                
                // Get all users
                using var connection = new NpgsqlConnection(_configuration.GetConnectionString("DefaultConnection"));
                var userIds = await connection.QueryAsync<int>("SELECT id FROM users");

                foreach (var userId in userIds)
                {
                    try
                    {
                        await syncService.SyncTransactionsAsync(userId, stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error syncing transactions for user {UserId}", userId);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred in Transactions Background Service.");
            }

            await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
        }
    }
}
