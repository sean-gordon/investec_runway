namespace GordonWorker.Models;

public class Transaction
{
    public Guid Id { get; set; }
    public string AccountId { get; set; } = string.Empty;
    public DateTimeOffset TransactionDate { get; set; }
    public string? Description { get; set; }
    public decimal Amount { get; set; }
    public decimal Balance { get; set; }
    public string? Category { get; set; }
    public bool IsAiProcessed { get; set; }
    public string? Notes { get; set; }

    public bool IsInternalTransfer()
    {
        if (string.Equals(Category, "TRANSFER", StringComparison.OrdinalIgnoreCase)) return true;
        if (Description != null && (
            Description.Contains("INT-ACC", StringComparison.OrdinalIgnoreCase) || 
            Description.Contains("INTERNAL TRANSFER", StringComparison.OrdinalIgnoreCase) ||
            Description.Contains("SAVINGS TO", StringComparison.OrdinalIgnoreCase) ||
            Description.Contains("TO SAVINGS", StringComparison.OrdinalIgnoreCase) ||
            Description.Contains("PAYED FROM", StringComparison.OrdinalIgnoreCase) ||
            Description.Contains("PAID FROM", StringComparison.OrdinalIgnoreCase))) 
            return true;
        return false;
    }
}
