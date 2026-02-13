using Dapper;
using GordonWorker.Models;
using Npgsql;

namespace GordonWorker.Services;

public interface ITransactionSyncService
{
    Task SyncTransactionsAsync(int userId, CancellationToken token = default);
    Task ForceRepullAsync(int userId);
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

    public async Task SyncTransactionsAsync(int userId, CancellationToken token = default)
    {
        var settings = await _settingsService.GetSettingsAsync(userId);
        
        // Ensure Client is configured for this user
        _client.Configure(settings.InvestecClientId, settings.InvestecSecret, settings.InvestecApiKey);

        using var connection = new NpgsqlConnection(_configuration.GetConnectionString("DefaultConnection"));
        await connection.OpenAsync(token);

        var accounts = await _client.GetAccountsAsync();
        if (accounts.Count == 0) return;

        // Check if DB is empty for this user
        var countSql = "SELECT COUNT(*) FROM transactions WHERE user_id = @userId";
        var transactionCount = await connection.ExecuteScalarAsync<int>(countSql, new { userId });
        
        var daysBack = transactionCount == 0 ? settings.HistoryDaysBack : settings.SyncBufferDays; 
        var fromDate = DateTimeOffset.UtcNow.AddDays(-daysBack);

        bool triggerReport = false;
        int totalNew = 0;

        foreach (var account in accounts)
        {
            var txs = await _client.GetTransactionsAsync(account.AccountId, fromDate);
            foreach (var tx in txs)
            {
                var insertSql = @"
                    INSERT INTO transactions (id, user_id, account_id, transaction_date, description, amount, balance, category, is_ai_processed)
                    VALUES (@Id, @UserId, @AccountId, @TransactionDate, @Description, @Amount, @Balance, @Category, @IsAiProcessed)
                    ON CONFLICT (id, transaction_date, user_id) DO NOTHING";

                var parameters = new
                {
                    tx.Id,
                    UserId = userId,
                    tx.AccountId,
                    TransactionDate = tx.TransactionDate.UtcDateTime,
                    tx.Description,
                    tx.Amount,
                    tx.Balance,
                    tx.Category,
                    tx.IsAiProcessed
                };

                var rowsAffected = await connection.ExecuteAsync(insertSql, parameters);
                if (rowsAffected > 0)
                {
                    totalNew++;
                    if (tx.Amount >= settings.UnexpectedPaymentThreshold)
                    {
                        var normalizedDesc = _actuarialService.NormalizeDescription(tx.Description);
                        if (!_actuarialService.IsFixedCost(normalizedDesc, settings) && !_actuarialService.IsSalary(tx, settings))
                            triggerReport = true;
                    }
                    if (tx.Amount <= -settings.IncomeAlertThreshold) triggerReport = true;
                }
            }
        }
        
        if (totalNew > 0) 
        {
            _logger.LogInformation("User {UserId}: Synced {Count} new transactions.", userId, totalNew);
            if (triggerReport) await _reportService.GenerateAndSendReportAsync(userId);
        }
    }

    public async Task ForceRepullAsync(int userId)
    {
        using var connection = new NpgsqlConnection(_configuration.GetConnectionString("DefaultConnection"));
        await connection.OpenAsync();
        await connection.ExecuteAsync("DELETE FROM transactions WHERE user_id = @userId", new { userId });
        await SyncTransactionsAsync(userId);
    }
}
