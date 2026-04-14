using MediatR;
using GordonWorker.Events;
using GordonWorker.Services;

namespace GordonWorker.Handlers;

public class TransactionNotificationHandler : INotificationHandler<TransactionsSyncedEvent>
{
    private readonly IActuarialService _actuarialService;
    private readonly ISettingsService _settingsService;
    private readonly ITelegramService _telegramService;
    private readonly IFinancialReportService _reportService;
    private readonly ISubscriptionService _subscriptionService;

    public TransactionNotificationHandler(
        IActuarialService actuarialService,
        ISettingsService settingsService,
        ITelegramService telegramService,
        IFinancialReportService reportService,
        ISubscriptionService subscriptionService)
    {
        _actuarialService = actuarialService;
        _settingsService = settingsService;
        _telegramService = telegramService;
        _reportService = reportService;
        _subscriptionService = subscriptionService;
    }

    public async Task Handle(TransactionsSyncedEvent notification, CancellationToken cancellationToken)
    {
        var allNewTxs = notification.Transactions;
        var userId = notification.UserId;

        if (allNewTxs.Count == 0) return;

        var settings = await _settingsService.GetSettingsAsync(userId);
        
        bool triggerReport = false;
        var pendingAlerts = new List<string>();

        if (!notification.Silent)
        {
            foreach (var tx in allNewTxs)
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

        if (triggerReport) 
        {
            await _reportService.GenerateAndSendReportAsync(userId);
        }
        
        // Trigger Subscription Check
        await _subscriptionService.CheckSubscriptionsAsync(userId);
    }
}