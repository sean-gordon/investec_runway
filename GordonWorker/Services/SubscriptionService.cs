using Dapper;
using GordonWorker.Models;
using Npgsql;
using System.Text.RegularExpressions;

namespace GordonWorker.Services;

public interface ISubscriptionService
{
    Task CheckSubscriptionsAsync(int userId);
}

public class SubscriptionService : ISubscriptionService
{
    private readonly IConfiguration _configuration;
    private readonly IActuarialService _actuarialService;
    private readonly ITelegramService _telegramService;
    private readonly ISettingsService _settingsService;
    private readonly ILogger<SubscriptionService> _logger;

    public SubscriptionService(IConfiguration configuration, IActuarialService actuarialService, ITelegramService telegramService, ISettingsService settingsService, ILogger<SubscriptionService> logger)
    {
        _configuration = configuration;
        _actuarialService = actuarialService;
        _telegramService = telegramService;
        _settingsService = settingsService;
        _logger = logger;
    }

    public async Task CheckSubscriptionsAsync(int userId)
    {
        try
        {
            var settings = await _settingsService.GetSettingsAsync(userId);
            using var connection = new NpgsqlConnection(_configuration.GetConnectionString("DefaultConnection"));
            
            // Fetch last 90 days of potential subscription candidates (repeating descriptions)
            var sql = @"
                SELECT * FROM transactions 
                WHERE user_id = @userId 
                AND transaction_date >= NOW() - INTERVAL '90 days'
                AND amount > 0 -- Expenses only
                ORDER BY transaction_date DESC";

            var transactions = (await connection.QueryAsync<Transaction>(sql, new { userId })).ToList();
            
            // Group by normalized description
            var grouped = transactions
                .GroupBy(t => _actuarialService.NormalizeDescription(t.Description))
                .Where(g => g.Count() >= 2) // Must appear at least twice to be a sub
                .ToList();

            foreach (var group in grouped)
            {
                var sorted = group.OrderByDescending(t => t.TransactionDate).ToList();
                var latest = sorted[0];
                var previous = sorted[1]; // Compare with immediate predecessor

                // Only alert if latest transaction is very recent (last 24h) to avoid spamming old alerts
                if ((DateTime.UtcNow - latest.TransactionDate.UtcDateTime).TotalHours > 24) continue;

                // Check for price creep (e.g. > 1% increase)
                // Ignore if it looks like a variable expense (e.g. Uber, Checkers) - heuristic check
                // Subscriptions usually have EXACT amounts or very close. 
                // But we are looking for creep, so we expect change.
                // Heuristic: If variance is > 0 and < 15% (inflationary bump), alert. 
                // If it doubles, it might be double usage (two Ubers).
                
                decimal increase = latest.Amount - previous.Amount;
                if (increase > 0)
                {
                    decimal percent = (increase / previous.Amount) * 100;
                    
                    // Alert threshold: Increase is between 0.1% and 20% (likely price hike, not usage spike)
                    // And explicitly ignore "Groceries" or known variable categories if possible, but we don't have tags yet.
                    // Use stability check from ActuarialService? No, circular dependency potential logic.
                    // Let's stick to simple math.
                    
                    if (percent > 0.5m && percent < 25m)
                    {
                        // Escape Markdown reserved characters in the group key to prevent parsing errors
                        var safeKey = group.Key
                            .Replace("_", "\\_")
                            .Replace("*", "\\*")
                            .Replace("[", "\\[")
                            .Replace("`", "\\`");

                        var msg = $"⚠️ **Subscription Creep Detected**\n" +
                                  $"**{safeKey}** increased by {percent:F1}% ({latest.Amount:C} vs {previous.Amount:C}).\n" +
                                  "Check if this is a contract increase.";
                        
                        await _telegramService.SendMessageAsync(userId, msg);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking subscriptions for user {UserId}", userId);
        }
    }
}
