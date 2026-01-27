namespace GordonWorker.Services;

public interface ISystemStatusService
{
    bool IsInvestecOnline { get; set; }
    DateTime LastInvestecCheck { get; set; }
    string LastError { get; set; }
}

public class SystemStatusService : ISystemStatusService
{
    public bool IsInvestecOnline { get; set; } = false;
    public DateTime LastInvestecCheck { get; set; } = DateTime.MinValue;
    public string LastError { get; set; } = string.Empty;
}
