using MediatR;
using GordonWorker.Events;
using GordonWorker.Services;
using GordonWorker.Repositories;

namespace GordonWorker.Handlers;

public class TransactionCategorizationHandler : INotificationHandler<TransactionsSyncedEvent>
{
    private readonly ITransactionClassifierService _classifier;
    private readonly ITransactionRepository _repository;
    private readonly ILogger<TransactionCategorizationHandler> _logger;

    public TransactionCategorizationHandler(
        ITransactionClassifierService classifier,
        ITransactionRepository repository,
        ILogger<TransactionCategorizationHandler> logger)
    {
        _classifier = classifier;
        _repository = repository;
        _logger = logger;
    }

    public async Task Handle(TransactionsSyncedEvent notification, CancellationToken cancellationToken)
    {
        var allNewTxs = notification.Transactions;
        var userId = notification.UserId;

        if (allNewTxs.Count > 0)
        {
            if (allNewTxs.Count <= 50 || notification.ForceCategorizeAll)
            {
                try
                {
                    _logger.LogInformation("User {UserId}: Categorizing {Count} new transactions with AI...", userId, allNewTxs.Count);
                    await _classifier.CategorizeTransactionsAsync(userId, allNewTxs);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "User {UserId}: AI categorization failed or service offline. Transactions will be stored uncategorized and retried in background.", userId);
                }
            }
            else
            {
                _logger.LogWarning("User {UserId}: Skipping AI categorization for {Count} transactions (batch too large). Will process in background.", userId, allNewTxs.Count);
            }
        }

        // --- BACKGROUND AUTOCATEGORIZATION ---
        if (!notification.ForceCategorizeAll)
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
}