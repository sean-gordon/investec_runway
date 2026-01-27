namespace GordonWorker.Models;

public class Transaction
{
    public Guid Id { get; set; }
    public DateTimeOffset TransactionDate { get; set; }
    public string? Description { get; set; }
    public decimal Amount { get; set; }
    public decimal Balance { get; set; }
    public string? Category { get; set; }
    public bool IsAiProcessed { get; set; }
}
