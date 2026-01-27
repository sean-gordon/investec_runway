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
        _logger.LogInformation("Transactions Background Service starting.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessTransactionsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred while processing transactions.");
            }

            await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
        }
    }

    private async Task ProcessTransactionsAsync(CancellationToken token)
    {
        using var scope = _serviceProvider.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<IInvestecClient>();
        
        // 1. Fetch Accounts
        var accounts = await client.GetAccountsAsync();
        
        if (accounts.Count == 0)
        {
            _logger.LogWarning("No accounts found for the configured Investec credentials.");
            return;
        }

        _logger.LogInformation("Found {Count} accounts. Starting sync...", accounts.Count);

        var connectionString = _configuration.GetConnectionString("DefaultConnection");
        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(token);

        var totalIngested = 0;

        // Check if we need a deep sync (empty table)
        var countSql = "SELECT COUNT(*) FROM transactions";
        var transactionCount = await connection.ExecuteScalarAsync<int>(countSql);
        
        // If empty, fetch last 180 days. Otherwise, just last 10 minutes (buffer).
        var fetchWindow = transactionCount == 0 ? TimeSpan.FromDays(180) : TimeSpan.FromMinutes(10);
        var fromDate = DateTimeOffset.UtcNow.Subtract(fetchWindow);

        _logger.LogInformation("Syncing transactions from: {FromDate} (Deep Sync: {IsDeep})", fromDate, transactionCount == 0);

        foreach (var account in accounts)
        {
            var transactions = await client.GetTransactionsAsync(account.AccountId, fromDate);
            
            if (transactions.Count == 0) continue;

            var sql = @"
                INSERT INTO transactions (id, account_id, transaction_date, description, amount, balance, category, is_ai_processed)
                VALUES (@Id, @AccountId, @TransactionDate, @Description, @Amount, @Balance, @Category, @IsAiProcessed)
                ON CONFLICT (id) DO NOTHING";

            var count = await connection.ExecuteAsync(sql, transactions);
            totalIngested += count;
        }

        if (totalIngested > 0)
        {
            _logger.LogInformation("Ingested {Count} new transactions across all accounts.", totalIngested);
        }
        else 
        {
             _logger.LogInformation("No new transactions found.");
        }
    }
}