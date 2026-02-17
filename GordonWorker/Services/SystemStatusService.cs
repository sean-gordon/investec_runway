namespace GordonWorker.Services;

public interface ISystemStatusService
{
    bool IsInvestecOnline { get; set; }
    DateTime LastInvestecCheck { get; set; }
    
    bool IsAiPrimaryOnline { get; set; }
    bool IsAiFallbackOnline { get; set; }
    DateTime LastAiCheck { get; set; }
    
    bool IsDatabaseOnline { get; set; }
    
    DateTime LastTelegramHit { get; set; }
    string LastTelegramError { get; set; }
    string LastError { get; set; }
}

public class SystemStatusService : ISystemStatusService
{
    public bool IsInvestecOnline { get; set; } = false;
    public DateTime LastInvestecCheck { get; set; } = DateTime.MinValue;
    
    public bool IsAiPrimaryOnline { get; set; } = false;
    public bool IsAiFallbackOnline { get; set; } = false;
    public DateTime LastAiCheck { get; set; } = DateTime.MinValue;
    
    public bool IsDatabaseOnline { get; set; } = true;
    
    public DateTime LastTelegramHit { get; set; } = DateTime.MinValue;
    public string LastTelegramError { get; set; } = string.Empty;
    public string LastError { get; set; } = string.Empty;
}
