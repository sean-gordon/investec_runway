using Dapper;
using GordonWorker.Models;
using GordonWorker.Repositories;
using GordonWorker.Events;
using MediatR;
using Npgsql;

namespace GordonWorker.Services;

public interface ITransactionSyncService
{
    Task SyncTransactionsAsync(int userId, bool silent = false, bool forceCategorizeAll = false, CancellationToken token = default);
    Task ForceRepullAsync(int userId);
}

public class TransactionSyncService : ITransactionSyncService
{
    private readonly IInvestecClient _client;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TransactionSyncService> _logger;
    private readonly ISettingsService _settingsService;
    private readonly ITransactionRepository _repository;
    private readonly IMediator _mediator;

    public TransactionSyncService(
        IInvestecClient client, 
        IConfiguration configuration, 
        ILogger<TransactionSyncService> logger,
        ISettingsService settingsService,
        ITransactionRepository repository,
        IMediator mediator)
    {
        _client = client;
        _configuration = configuration;
        _logger = logger;
        _settingsService = settingsService;
        _repository = repository;
        _mediator = mediator;
    }

    public async Task SyncTransactionsAsync(int userId, bool silent = false, bool forceCategorizeAll = false, CancellationToken token = default)
    {
        var settings = await _settingsService.GetSettingsAsync(userId);
        
        // Ensure Client is configured for this user
        _client.Configure(settings.InvestecClientId, settings.InvestecSecret, settings.InvestecApiKey);

        using var connection = new NpgsqlConnection(_configuration.GetConnectionString("DefaultConnection"));
        await connection.OpenAsync(token);

        var accounts = await _client.GetAccountsAsync();
        if (accounts.Count == 0) return;

        // Check if DB is empty for this user
        var transactionCount = await _repository.GetTransactionCountAsync(userId);
        
        // Initial sync or force repull should always be silent to prevent notification storms
        if (transactionCount == 0) silent = true;

        // If fewer than 50 transactions exist, treat as effectively empty and pull full history
        // This prevents the "only today's data" bug if an initial sync was interrupted
        var daysBack = transactionCount < 50 ? settings.HistoryDaysBack : settings.SyncBufferDays; 
        var fromDate = DateTimeOffset.UtcNow.AddDays(-daysBack);

        int totalNew = 0;

        // Fetch every account's transactions in parallel — they're independent network calls
        // against Investec, so the previous sequential loop was paying N round-trips of latency
        // for nothing. Same for the existing-id lookups.
        var accountTxTasks = accounts.Select(async acc =>
        {
            var txs = await _client.GetTransactionsAsync(acc.AccountId, fromDate);
            var existingIds = await _repository.GetExistingTransactionIdsAsync(userId, acc.AccountId, fromDate);
            var newTxs = txs.Where(t => !existingIds.Contains(t.Id)).ToList();
            _logger.LogInformation("User {UserId}: Fetched {Count} ({New} new) for account {AccountId} from {FromDate}",
                userId, txs.Count, newTxs.Count, acc.AccountId, fromDate);
            return newTxs;
        }).ToList();

        var perAccountNew = await Task.WhenAll(accountTxTasks);
        var allNewTxs = perAccountNew.SelectMany(x => x).ToList();

        if (allNewTxs.Count > 0)
        {
            // Bulk insert: one round-trip via unnest() instead of one INSERT per row.
            var inserted = await _repository.InsertTransactionsBatchAsync(allNewTxs, userId);
            totalNew = inserted;
            _logger.LogInformation("User {UserId}: Bulk-inserted {Inserted} of {Candidates} transactions.", userId, inserted, allNewTxs.Count);
        }
        
        if (totalNew > 0) 
        {
            _logger.LogInformation("User {UserId}: Sync complete. {Count} new records added to ledger.", userId, totalNew);
            
            // Publish event to handle side effects asynchronously
            await _mediator.Publish(new TransactionsSyncedEvent
            {
                UserId = userId,
                Transactions = allNewTxs,
                Silent = silent,
                ForceCategorizeAll = forceCategorizeAll
            }, token);
        }
        else 
        {
            _logger.LogInformation("User {UserId}: Sync complete. No new transactions found.", userId);
        }
    }

    public async Task ForceRepullAsync(int userId)
    {
        await _repository.DeleteTransactionsByUserAsync(userId);
        await SyncTransactionsAsync(userId, silent: true, forceCategorizeAll: true);
    }
}