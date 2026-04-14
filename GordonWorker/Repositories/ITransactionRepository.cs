using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using GordonWorker.Models;

namespace GordonWorker.Repositories;

public interface ITransactionRepository
{
    Task<IEnumerable<int>> GetAllUserIdsAsync();
    
    Task<List<Transaction>> GetTransactionsByUserAsync(int userId);
    Task<List<Transaction>> GetTransactionsByUserAsync(int userId, int limit, int offset = 0);
    /// <summary>
    /// Returns transactions for a user from <paramref name="since"/> onwards. Use this on the
    /// hot paths (runway top-up, daily briefing, actuarial recompute) so we don't drag the
    /// user's entire history into memory just to compute a 60–120 day projection.
    /// </summary>
    Task<List<Transaction>> GetTransactionsByUserSinceAsync(int userId, DateTime since);
    Task UpdateTransactionCategoryAsync(Guid transactionId, string category);
    Task UpdateTransactionsAsync(IEnumerable<Transaction> transactions);
    /// <summary>
    /// Applies a category to many transactions in a single round-trip via PostgreSQL's
    /// <c>unnest</c> trick. Use this on the categorisation hot path instead of the per-row
    /// <see cref="UpdateTransactionCategoryAsync"/>: a 500-tx classifier pass collapses from
    /// 500 UPDATEs to 1.
    /// </summary>
    Task UpdateTransactionCategoriesBatchAsync(IEnumerable<(Guid Id, string Category)> updates);
    Task<int> GetTransactionCountAsync(int userId);
    Task<HashSet<Guid>> GetExistingTransactionIdsAsync(int userId, string accountId, DateTimeOffset fromDate);
    Task<int> InsertTransactionAsync(Transaction tx, int userId);
    /// <summary>
    /// Inserts many transactions in a single round-trip. Returns the count of rows that were
    /// actually new (i.e. not blocked by the ON CONFLICT DO NOTHING clause). Use this on the
    /// sync hot path so a 200-row backfill is one query, not 200.
    /// </summary>
    Task<int> InsertTransactionsBatchAsync(IEnumerable<Transaction> transactions, int userId);
    Task<List<Transaction>> GetUnprocessedTransactionsAsync(int userId, int limit);
    Task<List<Transaction>> GetTransactionsForCategorizationAsync(int userId);
    Task DeleteTransactionsByUserAsync(int userId);

    Task DeleteChatHistoryAsync(int userId);
    Task<int> InsertChatHistoryAsync(int userId, string messageText, bool isUser);
    Task<IEnumerable<(string MessageText, bool IsUser)>> GetRecentChatHistoryAsync(int userId, int limit = 10);
    
    Task<List<Transaction>> GetHistoryForAnalysisAsync(int userId, int days);
    Task<IEnumerable<dynamic>> GetChartDataAsync(int userId, string sql);
    Task UpdateTransactionNoteAsync(Guid transactionId, int userId, string note);

    // Returns up to `limit` (description, category) pairs from the user's already-categorised
    // transactions, intended as few-shot examples for the categorisation prompt. Distinct by
    // description to avoid blowing token budget on near-duplicates.
    Task<List<(string Description, string Category)>> GetCategorizationExamplesAsync(int userId, int limit = 30);
}
