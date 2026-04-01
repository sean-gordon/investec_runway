using GordonWorker.Models;

namespace GordonWorker.Repositories;

public interface ITransactionRepository
{
    Task<IEnumerable<int>> GetAllUserIdsAsync();
    
    Task<List<Transaction>> GetTransactionsByUserAsync(int userId);
    Task UpdateTransactionCategoryAsync(Guid transactionId, string category);
    Task<int> GetTransactionCountAsync(int userId);
    Task<HashSet<Guid>> GetExistingTransactionIdsAsync(int userId, string accountId, DateTimeOffset fromDate);
    Task<int> InsertTransactionAsync(Transaction tx, int userId);
    Task<List<Transaction>> GetUnprocessedTransactionsAsync(int userId, int limit);
    Task DeleteTransactionsByUserAsync(int userId);

    Task DeleteChatHistoryAsync(int userId);
    Task<int> AddChatHistoryAsync(int userId, string messageText, bool isUser);
    Task<IEnumerable<(string MessageText, bool IsUser)>> GetRecentChatHistoryAsync(int userId, int limit = 10);
}
