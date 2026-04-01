using System.Text.Json;
using GordonWorker.Models;
using GordonWorker.Prompts;
using GordonWorker.Repositories;

namespace GordonWorker.Services;

public interface ITransactionClassifierService
{
    Task<List<Transaction>> CategorizeTransactionsAsync(int userId, List<Transaction> transactions);
}

public class TransactionClassifierService : ITransactionClassifierService
{
    private readonly IAiService _aiService;
    private readonly ITransactionRepository _repository;
    private readonly ILogger<TransactionClassifierService> _logger;

    public TransactionClassifierService(IAiService aiService, ITransactionRepository repository, ILogger<TransactionClassifierService> logger)
    {
        _aiService = aiService;
        _repository = repository;
        _logger = logger;
    }

    public async Task<List<Transaction>> CategorizeTransactionsAsync(int userId, List<Transaction> transactions)
    {
        if (transactions == null || !transactions.Any()) return new List<Transaction>();

        // Batch processing to avoid prompt size limits and 429 errors
        const int batchSize = 50;
        for (int i = 0; i < transactions.Count; i += batchSize)
        {
            var batch = transactions.Skip(i).Take(batchSize).ToList();
            await CategorizeBatchAsync(userId, batch);
        }

        return transactions;
    }

    private async Task CategorizeBatchAsync(int userId, List<Transaction> batch)
    {
        var txData = batch.Select(t => new { t.Id, t.Description, t.Amount }).ToList();
        var txJson = JsonSerializer.Serialize(txData);
        var systemPrompt = SystemPrompts.GetCategorizationPrompt();

        try
        {
            var jsonResponse = await _aiService.GenerateCompletionAsync(userId, systemPrompt, $"TRANSACTIONS:\n{txJson}");
            
            var match = System.Text.RegularExpressions.Regex.Match(jsonResponse, @"```json\s*(.*?)\s*```", System.Text.RegularExpressions.RegexOptions.Singleline);
            var cleanJson = match.Success ? match.Groups[1].Value : jsonResponse.Trim();

            if (cleanJson.StartsWith("I'm") || cleanJson.Contains("error"))
            {
                _logger.LogWarning("AI returned a non-JSON response for categorization: {Response}", cleanJson);
                return;
            }

            using var doc = JsonDocument.Parse(cleanJson);
            var results = new Dictionary<Guid, string>();
            
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var idStr = item.GetProperty("id").GetString();
                if (Guid.TryParse(idStr, out var id))
                {
                    var cat = item.GetProperty("category").GetString() ?? "General";
                    results[id] = cat;
                }
            }

            foreach (var tx in batch)
            {
                if (results.TryGetValue(tx.Id, out var category))
                {
                    tx.Category = category;
                    tx.IsAiProcessed = true;
                    // Update in DB via repository
                    await _repository.UpdateTransactionCategoryAsync(tx.Id, category);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to batch categorize transactions for user {UserId}", userId);
        }
    }
}
