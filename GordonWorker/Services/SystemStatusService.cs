namespace GordonWorker.Services;

public interface ISystemStatusService
{
    bool IsInvestecOnline { get; set; }
    DateTime LastInvestecCheck { get; set; }
    DateTime LastTelegramHit { get; set; }
    string LastTelegramError { get; set; }
    string LastError { get; set; }
}

public class SystemStatusService : ISystemStatusService
{
    public bool IsInvestecOnline { get; set; } = false;
    public DateTime LastInvestecCheck { get; set; } = DateTime.MinValue;
    public DateTime LastTelegramHit { get; set; } = DateTime.MinValue;
    public string LastTelegramError { get; set; } = string.Empty;
    public string LastError { get; set; } = string.Empty;
}
