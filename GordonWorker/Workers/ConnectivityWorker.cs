using GordonWorker.Services;

namespace GordonWorker.Workers;

public class ConnectivityWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ConnectivityWorker> _logger;
    private readonly ISystemStatusService _statusService;

    public ConnectivityWorker(IServiceProvider serviceProvider, ILogger<ConnectivityWorker> logger, ISystemStatusService statusService)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _statusService = statusService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Connectivity Worker starting.");

        // Initial check on startup
        await CheckConnectivityAsync();

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await CheckConnectivityAsync();
        }
    }

    private async Task CheckConnectivityAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<IInvestecClient>();

        var isOnline = await client.TestConnectivityAsync();
        
        _statusService.IsInvestecOnline = isOnline;
        _statusService.LastInvestecCheck = DateTime.UtcNow;

        if (isOnline)
            _logger.LogInformation("Investec API is ONLINE.");
        else
            _logger.LogWarning("Investec API is OFFLINE.");
    }
}
