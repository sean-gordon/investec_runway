using Dapper;
using GordonWorker.Models;
using Npgsql;

namespace GordonWorker.Services;

public interface ITransactionSyncService
{
    Task SyncTransactionsAsync(CancellationToken token = default);
    Task ForceRepullAsync();
}

public class TransactionSyncService : ITransactionSyncService
{
    private readonly IInvestecClient _client;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TransactionSyncService> _logger;
    private readonly ISettingsService _settingsService;

    public TransactionSyncService(
        IInvestecClient client, 
        IConfiguration configuration, 
        ILogger<TransactionSyncService> logger,
        ISettingsService settingsService)
    {
        _client = client;
        _configuration = configuration;
        _logger = logger;
        _settingsService = settingsService;
    }

    public async Task SyncTransactionsAsync(CancellationToken token = default)
    {
        var settings = await _settingsService.GetSettingsAsync();
        using var connection = new NpgsqlConnection(_configuration.GetConnectionString("DefaultConnection"));
        await connection.OpenAsync(token);

        var accounts = await _client.GetAccountsAsync();
        if (accounts.Count == 0) return;

        // Check if DB is empty to decide depth
        var countSql = "SELECT COUNT(*) FROM transactions";
        var transactionCount = await connection.ExecuteScalarAsync<int>(countSql);
        
        // Use setting for "Deep Sync" depth
        var daysBack = transactionCount == 0 ? settings.HistoryDaysBack : 0.05; // 0.05 days is ~1 hour buffer
        var fromDate = DateTimeOffset.UtcNow.AddDays(-daysBack);

        if (transactionCount == 0)
        {
             _logger.LogInformation("Deep Sync Triggered: Fetching last {Days} days.", daysBack);
        }

        int total = 0;
        foreach (var account in accounts)
        {
            var txs = await _client.GetTransactionsAsync(account.AccountId, fromDate);
            if (txs.Count == 0) continue;

            var sql = @"
                INSERT INTO transactions (id, account_id, transaction_date, description, amount, balance, category, is_ai_processed)
                VALUES (@Id, @AccountId, @TransactionDate, @Description, @Amount, @Balance, @Category, @IsAiProcessed)
                ON CONFLICT (id) DO NOTHING";

            total += await connection.ExecuteAsync(sql, txs);
        }
        
        if (total > 0) _logger.LogInformation("Synced {Count} new transactions.", total);
    }

    public async Task ForceRepullAsync()
    {
        _logger.LogWarning("Force Repull Initiated: Truncating table and syncing.");
        
        using var connection = new NpgsqlConnection(_configuration.GetConnectionString("DefaultConnection"));
        await connection.OpenAsync();
        
        // Truncate to force clean slate
        await connection.ExecuteAsync("TRUNCATE TABLE transactions");

        // Run sync immediately
        await SyncTransactionsAsync();
    }
}
