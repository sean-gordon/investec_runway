using System.Text.Json;
using System.Text.RegularExpressions;
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

        // Step 1: group by normalised merchant key. "WOOLWORTHS #123", "WOOLWORTHS #456" and
        // "Woolworths JHB" all collapse to one key → we only ask the AI ONCE per merchant, then
        // fan the answer out to every matching transaction. Slashes token cost dramatically on
        // normal spending patterns (people visit the same merchants repeatedly).
        var groups = transactions
            .Where(t => !string.IsNullOrWhiteSpace(t.Description))
            .GroupBy(t => NormaliseMerchant(t.Description!))
            .ToList();

        _logger.LogInformation("Categorising {TxCount} transactions across {GroupCount} unique merchants for user {UserId}.",
            transactions.Count, groups.Count, userId);

        // Step 2: load the user's own confirmed categorisations for few-shot context.
        List<(string Description, string Category)> examples;
        try
        {
            examples = await _repository.GetCategorizationExamplesAsync(userId, 30);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load few-shot examples for user {UserId}; continuing without.", userId);
            examples = new List<(string, string)>();
        }

        // Step 3: classify unique merchants in batches, then fan results back to every tx.
        // We send one representative tx per merchant (first in each group) as the payload.
        var representatives = groups.Select(g => g.First()).ToList();

        const int batchSize = 50;
        var merchantResults = new Dictionary<Guid, string>();
        for (int i = 0; i < representatives.Count; i += batchSize)
        {
            var batch = representatives.Skip(i).Take(batchSize).ToList();
            var batchResults = await CategorizeBatchAsync(userId, batch, examples);
            foreach (var kv in batchResults) merchantResults[kv.Key] = kv.Value;
        }

        // Step 4: fan out results to every transaction in each merchant group and persist.
        foreach (var group in groups)
        {
            var repId = group.First().Id;
            if (!merchantResults.TryGetValue(repId, out var category)) continue;

            foreach (var tx in group)
            {
                tx.Category = category;
                tx.IsAiProcessed = true;
                try
                {
                    await _repository.UpdateTransactionCategoryAsync(tx.Id, category);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to persist category for tx {TxId}", tx.Id);
                }
            }
        }

        return transactions;
    }

    private async Task<Dictionary<Guid, string>> CategorizeBatchAsync(
        int userId,
        List<Transaction> batch,
        List<(string Description, string Category)> examples)
    {
        var results = new Dictionary<Guid, string>();
        var txData = batch.Select(t => new { t.Id, t.Description, t.Amount }).ToList();
        var txJson = JsonSerializer.Serialize(txData);
        var systemPrompt = SystemPrompts.GetCategorizationPrompt(examples);

        try
        {
            var jsonResponse = await _aiService.GenerateCompletionAsync(userId, systemPrompt, $"TRANSACTIONS:\n{txJson}");

            var match = Regex.Match(jsonResponse, @"```json\s*(.*?)\s*```", RegexOptions.Singleline);
            var cleanJson = match.Success ? match.Groups[1].Value : jsonResponse.Trim();

            if (cleanJson.StartsWith("I'm") || cleanJson.Contains("error"))
            {
                _logger.LogWarning("AI returned a non-JSON response for categorization: {Response}", cleanJson);
                return results;
            }

            using var doc = JsonDocument.Parse(cleanJson);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var idStr = item.GetProperty("id").GetString();
                if (Guid.TryParse(idStr, out var id))
                {
                    var cat = item.GetProperty("category").GetString() ?? "General";
                    results[id] = cat;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to batch categorize transactions for user {UserId}", userId);
        }

        return results;
    }

    // Normalise a transaction description down to a stable merchant key. The goal is to collapse
    // "WOOLWORTHS #123 RANDBURG", "Woolworths Cape Town", and "WW FOOD JHB" into one bucket.
    // We're deliberately conservative: aggressive normalisation would merge different merchants
    // (e.g. "Shell" and "Shoprite"). Rules:
    //   1. Upper-case and strip all digits, punctuation, and extra whitespace.
    //   2. Drop common location/branch suffix tokens (JHB, CPT, etc.).
    //   3. Take the first two meaningful tokens (usually enough to identify a chain).
    private static readonly HashSet<string> LocationNoiseTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "JHB", "JOHANNESBURG", "CPT", "CAPETOWN", "DBN", "DURBAN", "PTA", "PRETORIA",
        "SANDTON", "ROSEBANK", "RANDBURG", "CENTURION", "ZA", "RSA", "SOUTH", "AFRICA",
        "BRANCH", "ATM", "POS", "PURCHASE", "PMT", "PAY", "PAYMENT", "CARD"
    };

    private static string NormaliseMerchant(string description)
    {
        if (string.IsNullOrWhiteSpace(description)) return "";
        var upper = description.ToUpperInvariant();
        // Strip anything that isn't a letter or space.
        var cleaned = Regex.Replace(upper, @"[^A-Z\s]", " ");
        var tokens = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 2 && !LocationNoiseTokens.Contains(t))
            .Take(2)
            .ToArray();
        return tokens.Length == 0 ? upper.Trim() : string.Join(" ", tokens);
    }
}
