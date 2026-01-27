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
        var accountId = _configuration["INVESTEC_ACCOUNT_ID"];
        
        if (string.IsNullOrEmpty(accountId))
        {
            _logger.LogWarning("INVESTEC_ACCOUNT_ID is not set.");
            return;
        }

        // Fetch transactions from 5 minutes ago
        var fromDate = DateTimeOffset.UtcNow.AddMinutes(-5);
        
        _logger.LogInformation("Fetching transactions since {FromDate}", fromDate);
        
        var transactions = await client.GetTransactionsAsync(accountId, fromDate);
        
        if (transactions.Count == 0)
        {
            _logger.LogInformation("No new transactions found.");
            return;
        }

        var connectionString = _configuration.GetConnectionString("DefaultConnection");
        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(token);

        var sql = @"
            INSERT INTO transactions (id, transaction_date, description, amount, balance, category, is_ai_processed)
            VALUES (@Id, @TransactionDate, @Description, @Amount, @Balance, @Category, @IsAiProcessed)
            ON CONFLICT (id) DO NOTHING";

        var count = await connection.ExecuteAsync(sql, transactions);

        _logger.LogInformation("Ingested {Count} new transactions.", count);
    }
}
