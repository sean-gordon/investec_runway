using GordonWorker.Services;

namespace GordonWorker.Workers;

public class DailyBriefingWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<DailyBriefingWorker> _logger;

    public DailyBriefingWorker(IServiceProvider serviceProvider, ILogger<DailyBriefingWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Daily Briefing Worker starting.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.Now;
                // Target 08:00 AM
                var nextRun = now.Date.AddHours(8);
                if (now >= nextRun) nextRun = nextRun.AddDays(1);

                var delay = nextRun - now;
                _logger.LogInformation("Daily Briefing scheduled for {NextRun} (in {Delay}).", nextRun, delay);

                await Task.Delay(delay, stoppingToken);

                await SendDailyBriefingsAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Daily Briefing Worker loop.");
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); // Retry delay
            }
        }
    }

    private async Task SendDailyBriefingsAsync(CancellationToken token)
    {
        using var scope = _serviceProvider.CreateScope();
        var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();

        // Get all users
        using var db = new Npgsql.NpgsqlConnection(config.GetConnectionString("DefaultConnection"));
        var userIds = await Dapper.SqlMapper.QueryAsync<int>(db, "SELECT id FROM users");

        var tasks = userIds.Select(userId => SendSingleBriefingAsync(userId, token));
        await Task.WhenAll(tasks);
    }

    private async Task SendSingleBriefingAsync(int userId, CancellationToken token)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var settingsService = scope.ServiceProvider.GetRequiredService<ISettingsService>();
            var telegramService = scope.ServiceProvider.GetRequiredService<ITelegramService>();
            var actuarialService = scope.ServiceProvider.GetRequiredService<IActuarialService>();
            var investecClient = scope.ServiceProvider.GetRequiredService<IInvestecClient>();
            var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
            var aiService = scope.ServiceProvider.GetRequiredService<IAiService>();

            var settings = await settingsService.GetSettingsAsync(userId);
            if (string.IsNullOrWhiteSpace(settings.TelegramBotToken) || string.IsNullOrWhiteSpace(settings.TelegramChatId)) return;

            // Sync data first (light sync)
            investecClient.Configure(settings.InvestecClientId, settings.InvestecSecret, settings.InvestecApiKey);
            var accounts = await investecClient.GetAccountsAsync();
            if (!accounts.Any()) return;

            decimal currentBalance = 0;
            foreach (var acc in accounts) currentBalance += await investecClient.GetAccountBalanceAsync(acc.AccountId);

            using var db = new Npgsql.NpgsqlConnection(config.GetConnectionString("DefaultConnection"));
            // Get minimal history for context
            var history = (await Dapper.SqlMapper.QueryAsync<Models.Transaction>(db,
                "SELECT * FROM transactions WHERE user_id = @userId AND transaction_date >= NOW() - INTERVAL '60 days' ORDER BY transaction_date ASC",
                new { userId })).ToList();

            var summary = await actuarialService.AnalyzeHealthAsync(history, currentBalance, settings);

            // Generate Briefing with AI
            var prompt = $@"You are {settings.SystemPersona}, the user's Personal CFO.
It is 8:00 AM. Provide a 2-sentence morning briefing.
- Current Balance: R{currentBalance:N2}
- Days to Payday: {summary.DaysUntilNextSalary}
- Runway: {summary.ExpectedRunwayDays:F0} days
- Upcoming Bills: R{summary.UpcomingExpectedPayments:N2}

INSTRUCTIONS:
- Be concise and professional.
- If runway < days to payday, warn them gently.
- If bills are high, remind them.
- Otherwise, wish them a productive day.
- Do NOT use 'Subject:' lines.";

            var briefing = await aiService.FormatResponseAsync(userId, prompt, "", isWhatsApp: false);
            
            if (!string.IsNullOrWhiteSpace(briefing))
            {
                await telegramService.SendMessageAsync(userId, $"🌅 <b>Morning Briefing</b>\n\n{briefing}");
            }
            else 
            {
                _logger.LogWarning("Skipping daily briefing for user {UserId} due to AI failure.", userId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send daily briefing for user {UserId}", userId);
        }
    }
}
