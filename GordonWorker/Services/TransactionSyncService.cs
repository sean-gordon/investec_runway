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
    private readonly IActuarialService _actuarialService;
    private readonly IFinancialReportService _reportService;

    public TransactionSyncService(
        IInvestecClient client, 
        IConfiguration configuration, 
        ILogger<TransactionSyncService> logger,
        ISettingsService settingsService,
        IActuarialService actuarialService,
        IFinancialReportService reportService)
    {
        _client = client;
        _configuration = configuration;
        _logger = logger;
        _settingsService = settingsService;
        _actuarialService = actuarialService;
        _reportService = reportService;
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
        var daysBack = transactionCount == 0 ? settings.HistoryDaysBack : settings.SyncBufferDays; 
        var fromDate = DateTimeOffset.UtcNow.AddDays(-daysBack);

        if (transactionCount == 0)
        {
             _logger.LogInformation("Deep Sync Triggered: Fetching last {Days} days.", daysBack);
        }

        bool triggerReport = false;
        int totalNew = 0;

        foreach (var account in accounts)
        {
            var txs = await _client.GetTransactionsAsync(account.AccountId, fromDate);
            if (txs.Count == 0) continue;

            foreach (var tx in txs)
            {
                var insertSql = @"
                    INSERT INTO transactions (id, account_id, transaction_date, description, amount, balance, category, is_ai_processed)
                    VALUES (@Id, @AccountId, @TransactionDate, @Description, @Amount, @Balance, @Category, @IsAiProcessed)
                    ON CONFLICT (id) DO NOTHING";

                var rowsAffected = await connection.ExecuteAsync(insertSql, tx);
                
                // If it's a NEW transaction (rowsAffected > 0)
                if (rowsAffected > 0)
                {
                    totalNew++;

                    // Check for Unexpected Large Payment Trigger
                    // Large debits are positive in Investec API
                    if (tx.Amount >= settings.UnexpectedPaymentThreshold)
                    {
                        var normalizedDesc = _actuarialService.NormalizeDescription(tx.Description);
                        bool isFixed = _actuarialService.IsFixedCost(normalizedDesc, settings);
                        bool isSalary = _actuarialService.IsSalary(tx, settings);

                        if (!isFixed && !isSalary)
                        {
                            _logger.LogWarning("Unexpected Large Payment Detected: {Desc} (R{Amount}). Triggering automated report.", tx.Description, tx.Amount);
                            triggerReport = true;
                        }
                    }

                    // Check for Large Income Alert
                    // Credits are negative in Investec API
                    if (tx.Amount <= -settings.IncomeAlertThreshold)
                    {
                        _logger.LogInformation("Large Income Detected: {Desc} (R{Amount}). Triggering automated report.", tx.Description, Math.Abs(tx.Amount));
                        triggerReport = true;
                    }
                }
            }
        }
        
        if (totalNew > 0) 
        {
            _logger.LogInformation("Synced {Count} new transactions.", totalNew);
            
            if (triggerReport)
            {
                await _reportService.GenerateAndSendReportAsync();
            }
        }
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
