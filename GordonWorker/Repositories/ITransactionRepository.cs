using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using GordonWorker.Models;

namespace GordonWorker.Repositories;

public interface ITransactionRepository
{
    Task<IEnumerable<int>> GetAllUserIdsAsync();
    
    Task<List<Transaction>> GetTransactionsByUserAsync(int userId);
    Task<List<Transaction>> GetTransactionsByUserAsync(int userId, int limit);
    Task UpdateTransactionCategoryAsync(Guid transactionId, string category);
    Task UpdateTransactionsAsync(IEnumerable<Transaction> transactions);
    Task<int> GetTransactionCountAsync(int userId);
    Task<HashSet<Guid>> GetExistingTransactionIdsAsync(int userId, string accountId, DateTimeOffset fromDate);
    Task<int> InsertTransactionAsync(Transaction tx, int userId);
    Task<List<Transaction>> GetUnprocessedTransactionsAsync(int userId, int limit);
    Task<List<Transaction>> GetTransactionsForCategorizationAsync(int userId);
    Task DeleteTransactionsByUserAsync(int userId);

    Task DeleteChatHistoryAsync(int userId);
    Task<int> InsertChatHistoryAsync(int userId, string messageText, bool isUser);
    Task<IEnumerable<(string MessageText, bool IsUser)>> GetRecentChatHistoryAsync(int userId, int limit = 10);
    
    Task<List<Transaction>> GetHistoryForAnalysisAsync(int userId, int days);
    Task<IEnumerable<dynamic>> GetChartDataAsync(int userId, string sql);
    Task UpdateTransactionNoteAsync(Guid transactionId, string note);
}
