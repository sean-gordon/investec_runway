using Dapper;
using GordonWorker.Models;
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
    private readonly IActuarialService _actuarialService;
    private readonly IFinancialReportService _reportService;
    private readonly ISubscriptionService _subscriptionService;
    private readonly ITelegramService _telegramService;
    private readonly ITransactionRepository _repository;
    private readonly ITransactionClassifierService _classifier;

    public TransactionSyncService(
        IInvestecClient client, 
        IConfiguration configuration, 
        ILogger<TransactionSyncService> logger,
        ISettingsService settingsService,
        IActuarialService actuarialService,
        IFinancialReportService reportService,
        ISubscriptionService subscriptionService,
        ITelegramService telegramService,
        ITransactionRepository repository,
        ITransactionClassifierService classifier)
    {
        _client = client;
        _configuration = configuration;
        _logger = logger;
        _settingsService = settingsService;
        _actuarialService = actuarialService;
        _reportService = reportService;
        _subscriptionService = subscriptionService;
        _telegramService = telegramService;
        _repository = repository;
        _classifier = classifier;
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

        bool triggerReport = false;
        int totalNew = 0;
        var pendingAlerts = new List<string>();

        foreach (var account in accounts)
        {
            var txs = await _client.GetTransactionsAsync(account.AccountId, fromDate);
            _logger.LogInformation("User {UserId}: Fetched {Count} transactions for account {AccountId} from {FromDate}", userId, txs.Count, account.AccountId, fromDate);

            // Filter out transactions already in DB to avoid unnecessary AI calls
            var existingIds = await _repository.GetExistingTransactionIdsAsync(userId, account.AccountId, fromDate);

            var newTxs = txs.Where(t => !existingIds.Contains(t.Id)).ToList();
            
            if (newTxs.Any())
            {
                if (newTxs.Count <= 50 || forceCategorizeAll)
                {
                    try
                    {
                        _logger.LogInformation("User {UserId}: Categorizing {Count} new transactions with AI...", userId, newTxs.Count);
                        await _classifier.CategorizeTransactionsAsync(userId, newTxs);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "User {UserId}: AI categorization failed or service offline. Transactions will be stored uncategorized and retried in background.", userId);
                    }
                }
                else
                {
                    _logger.LogWarning("User {UserId}: Skipping AI categorization for {Count} transactions (batch too large). Guarding against timeouts. Will process in background.", userId, newTxs.Count);
                }
            }

            foreach (var tx in newTxs)
            {
                var rowsAffected = await _repository.InsertTransactionAsync(tx, userId);
                if (rowsAffected > 0)
                {
                    totalNew++;
                    _logger.LogDebug("User {UserId}: New transaction inserted: {Id} - {Description} ({Category})", userId, tx.Id, tx.Description, tx.Category);
                    
                    if (!silent)
                    {
                        if (tx.Amount <= -settings.UnexpectedPaymentThreshold)
                        {
                            var normalizedDesc = _actuarialService.NormalizeDescription(tx.Description);
                            if (!_actuarialService.IsFixedCost(normalizedDesc, settings) && !_actuarialService.IsSalary(tx, settings))
                            {
                                triggerReport = true;
                                pendingAlerts.Add($"🚨 <b>High Spend:</b> {TelegramService.EscapeHtml(tx.Description)} (R{Math.Abs(tx.Amount):N2})");
                            }
                        }
                        if (tx.Amount >= settings.IncomeAlertThreshold) 
                        {
                            triggerReport = true;
                            pendingAlerts.Add($"💰 <b>Large Income:</b> {TelegramService.EscapeHtml(tx.Description)} (R{tx.Amount:N2})");
                        }
                    }
                }
            }
        }
        
        if (totalNew > 0) 
        {
            _logger.LogInformation("User {UserId}: Sync complete. {Count} new records added to ledger.", userId, totalNew);
            
            // Handle Alerts (Aggregation to prevent flooding)
            if (pendingAlerts.Any())
            {
                if (pendingAlerts.Count > 2)
                {
                    await _telegramService.SendMessageAsync(userId, 
                        $"🔔 <b>Activity Summary</b>\nI have detected {pendingAlerts.Count} significant transactions in this sync. I am generating a full briefing for your review.");
                }
                else 
                {
                    foreach (var msg in pendingAlerts)
                    {
                        await _telegramService.SendMessageAsync(userId, msg + "\n\nWhat was this for?");
                    }
                }
            }

            if (triggerReport) await _reportService.GenerateAndSendReportAsync(userId);
            
            // Trigger Subscription Check
            await _subscriptionService.CheckSubscriptionsAsync(userId);
        }
        else 
        {
            _logger.LogInformation("User {UserId}: Sync complete. No new transactions found.", userId);
        }

        // --- BACKGROUND AUTOCATEGORIZATION ---
        // Slowly chip away at the unprocessed backlog (or transactions that have "General" or no category)
        if (!forceCategorizeAll)
        {
            var uncategorized = await _repository.GetUnprocessedTransactionsAsync(userId, 50);
            if (uncategorized.Any())
            {
                try
                {
                    _logger.LogInformation("User {UserId}: Processing background backlog of {Count} uncategorized/undetermined transactions.", userId, uncategorized.Count);
                    await _classifier.CategorizeTransactionsAsync(userId, uncategorized);
                    _logger.LogInformation("User {UserId}: Background categorization step complete.", userId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "User {UserId}: Background AI categorization failed. Will retry in next sync cycle.", userId);
                }
            }
        }
    }

    public async Task ForceRepullAsync(int userId)
    {
        await _repository.DeleteTransactionsByUserAsync(userId);
        await SyncTransactionsAsync(userId, silent: true, forceCategorizeAll: true);
    }
}
