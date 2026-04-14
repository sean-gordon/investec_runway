using MediatR;
using GordonWorker.Models;

namespace GordonWorker.Events;

public class TransactionsSyncedEvent : INotification
{
    public int UserId { get; set; }
    public List<Transaction> Transactions { get; set; } = new();
    public bool Silent { get; set; }
    public bool ForceCategorizeAll { get; set; }
}