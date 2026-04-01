using Dapper;
using GordonWorker.Models;
using GordonWorker.Services;
using Npgsql;

namespace GordonWorker.Workers;

public class RunwayTopUpWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<RunwayTopUpWorker> _logger;
    private readonly TelegramChatService _telegramChatService;

    public RunwayTopUpWorker(IServiceProvider serviceProvider, ILogger<RunwayTopUpWorker> logger, TelegramChatService telegramChatService)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _telegramChatService = telegramChatService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Runway Top-Up Worker starting.");
        
        // Initial delay to allow DB and other services to start up
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAndTopUpAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Runway Top-Up Worker.");
            }

            // Run daily
            await Task.Delay(TimeSpan.FromDays(1), stoppingToken);
        }
    }

    private async Task CheckAndTopUpAsync(CancellationToken token)
    {
        IEnumerable<int> users;
        using (var scope = _serviceProvider.CreateScope())
        {
            var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
            using var connection = new NpgsqlConnection(config.GetConnectionString("DefaultConnection"));
            users = await connection.QueryAsync<int>("SELECT id FROM users");
        }

        foreach (var userId in users)
        {
            if (token.IsCancellationRequested) break;

            using var userScope = _serviceProvider.CreateScope();
            var settingsService = userScope.ServiceProvider.GetRequiredService<ISettingsService>();
            var actuarial = userScope.ServiceProvider.GetRequiredService<IActuarialService>();
            var investecClient = userScope.ServiceProvider.GetRequiredService<IInvestecClient>();
            var config = userScope.ServiceProvider.GetRequiredService<IConfiguration>();

            var settings = await settingsService.GetSettingsAsync(userId);
            
            // Skip if not enabled or accounts are missing
            if (!settings.AutoTopUpEnabled || 
                string.IsNullOrEmpty(settings.SavingsAccountId) || 
                string.IsNullOrEmpty(settings.SpendingAccountId)) continue;

            try
            {
                // Fetch existing transactions from DB to calculate runway
                List<Transaction> history;
                decimal currentBalance = 0;
                
                using (var connection = new NpgsqlConnection(config.GetConnectionString("DefaultConnection")))
                {
                    history = (await connection.QueryAsync<Transaction>(
                        "SELECT * FROM transactions WHERE user_id = @UserId ORDER BY transaction_date DESC",
                        new { UserId = userId })).ToList();
                        
                    // Get latest balance of the spending account
                    var latestTx = history.FirstOrDefault(t => t.AccountId == settings.SpendingAccountId);
                    if (latestTx != null)
                    {
                        currentBalance = latestTx.Balance;
                    }
                    else
                    {
                        // Fallback to fetch from live if we have no history for spending account
                        _logger.LogWarning("No history for spending account, fetching live balance.");
                        investecClient.Configure(settings.InvestecClientId, settings.InvestecSecret, settings.InvestecApiKey, settings.InvestecBaseUrl, settings.InvestecEnvironment);
                        currentBalance = await investecClient.GetAccountBalanceAsync(settings.SpendingAccountId);
                    }
                }

                if (!history.Any()) continue;

                // Calculate Runway
                var health = await actuarial.AnalyzeHealthAsync(history, currentBalance, settings);
                
                _logger.LogInformation("[Top-Up Check] User {Id}: Runway is {Runway:F1} days (Threshold: {Threshold} days)", 
                    userId, health.ExpectedRunwayDays, settings.RunwayThresholdDays);

                if (health.ExpectedRunwayDays < settings.RunwayThresholdDays)
                {
                    // Trigger top-up
                    _logger.LogInformation("Triggering top-up of R{Amount} for User {Id}", settings.TopUpAmount, userId);
                    
                    investecClient.Configure(settings.InvestecClientId, settings.InvestecSecret, settings.InvestecApiKey, settings.InvestecBaseUrl, settings.InvestecEnvironment);
                    
                    var (success, error) = await investecClient.ExecuteTransferAsync(
                        fromAccountId: settings.SavingsAccountId,
                        toAccountId: settings.SpendingAccountId,
                        amount: settings.TopUpAmount,
                        reference: "Gordon Runway Top-Up",
                        isDryRun: settings.IsDryRunEnabled
                    );

                    string message = "";
                    if (success)
                    {
                        if (settings.IsDryRunEnabled)
                        {
                            message = $"🛡️ *Runway Top-Up (Dry Run)*\nYour runway dropped to {health.ExpectedRunwayDays:F1} days.\nI would have transferred R{settings.TopUpAmount:F2} from your Savings to your Spending account, but Dry-Run mode is enabled.";
                        }
                        else
                        {
                            message = $"💸 *Runway Top-Up Executed*\nYour runway dropped to {health.ExpectedRunwayDays:F1} days.\nI have successfully transferred R{settings.TopUpAmount:F2} from your Savings account to your Spending account to keep you afloat.";
                        }
                    }
                    else
                    {
                        message = $"⚠️ *Runway Top-Up Failed*\nAttempted to transfer R{settings.TopUpAmount:F2} because your runway is at {health.ExpectedRunwayDays:F1} days, but the transfer failed.\nError: {error}";
                    }

                    // Send Telegram notification if configured
                    if (!string.IsNullOrEmpty(settings.TelegramChatId))
                    {
                        _telegramChatService.EnqueueMessage(userId, message, "markdown");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to check or execute top-up for user {UserId}", userId);
            }
        }
    }
}
