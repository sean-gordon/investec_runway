using Dapper;
using GordonWorker.Models;
using Npgsql;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace GordonWorker.Repositories;

public class TransactionRepository : ITransactionRepository
{
    private readonly string _connectionString;

    public TransactionRepository(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection") 
            ?? throw new InvalidOperationException("DefaultConnection not found.");
    }

    public async Task<IEnumerable<int>> GetAllUserIdsAsync()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        return await connection.QueryAsync<int>("SELECT id FROM users");
    }

    public async Task<List<Transaction>> GetTransactionsByUserAsync(int userId)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        return (await connection.QueryAsync<Transaction>(
            "SELECT * FROM transactions WHERE user_id = @UserId ORDER BY transaction_date DESC",
            new { UserId = userId })).ToList();
    }

    public async Task<List<Transaction>> GetTransactionsByUserAsync(int userId, int limit)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        return (await connection.QueryAsync<Transaction>(
            "SELECT * FROM transactions WHERE user_id = @UserId ORDER BY transaction_date DESC LIMIT @Limit",
            new { UserId = userId, Limit = limit })).ToList();
    }

    public async Task<List<Transaction>> GetTransactionsByUserSinceAsync(int userId, DateTime since)
    {
        // Backed by idx_transactions_user_date — this should be a fast index range scan rather
        // than the full hypertable scan that the unbounded sibling produces on long histories.
        await using var connection = new NpgsqlConnection(_connectionString);
        return (await connection.QueryAsync<Transaction>(
            "SELECT * FROM transactions WHERE user_id = @UserId AND transaction_date >= @Since ORDER BY transaction_date DESC",
            new { UserId = userId, Since = since })).ToList();
    }

    public async Task UpdateTransactionCategoryAsync(Guid transactionId, string category)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.ExecuteAsync(
            "UPDATE transactions SET category = @Category, is_ai_processed = TRUE WHERE id = @Id",
            new { Category = category, Id = transactionId });
    }

    public async Task UpdateTransactionsAsync(IEnumerable<Transaction> transactions)
    {
        // Delegates to the batch path so the legacy callers also benefit. Filters out anything
        // without a category — those rows haven't actually been classified yet.
        var updates = transactions
            .Where(t => !string.IsNullOrEmpty(t.Category))
            .Select(t => (t.Id, t.Category!));
        await UpdateTransactionCategoriesBatchAsync(updates);
    }

    public async Task UpdateTransactionCategoriesBatchAsync(IEnumerable<(Guid Id, string Category)> updates)
    {
        var list = updates as IList<(Guid Id, string Category)> ?? updates.ToList();
        if (list.Count == 0) return;

        // Single-statement batch update via unnest. Postgres builds two parallel arrays from the
        // @Ids / @Categories parameters and joins them in the FROM clause — far cheaper than
        // round-tripping each row, and crucially still benefits from the partial index on
        // is_ai_processed because we only ever set it to TRUE.
        var ids = list.Select(u => u.Id).ToArray();
        var cats = list.Select(u => u.Category ?? "General").ToArray();

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.ExecuteAsync(@"
            UPDATE transactions AS t
            SET category = u.cat,
                is_ai_processed = TRUE
            FROM unnest(@Ids::uuid[], @Categories::text[]) AS u(id, cat)
            WHERE t.id = u.id;",
            new { Ids = ids, Categories = cats });
    }

    public async Task<int> GetTransactionCountAsync(int userId)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        return await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM transactions WHERE user_id = @userId", 
            new { userId });
    }

    public async Task<HashSet<Guid>> GetExistingTransactionIdsAsync(int userId, string accountId, DateTimeOffset fromDate)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        return (await connection.QueryAsync<Guid>(
            "SELECT id FROM transactions WHERE user_id = @userId AND account_id = @accountId AND transaction_date >= @fromDate",
            new { userId, accountId, fromDate })).ToHashSet();
    }

    public async Task<int> InsertTransactionAsync(Transaction tx, int userId)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        var insertSql = @"
            INSERT INTO transactions (id, user_id, account_id, transaction_date, description, amount, balance, category, is_ai_processed, notes)
            VALUES (@Id, @UserId, @AccountId, @TransactionDate, @Description, @Amount, @Balance, @Category, @IsAiProcessed, NULL)
            ON CONFLICT (id, transaction_date, user_id) DO NOTHING";

        return await connection.ExecuteAsync(insertSql, new {
            tx.Id,
            UserId = userId,
            tx.AccountId,
            TransactionDate = tx.TransactionDate.UtcDateTime,
            tx.Description,
            tx.Amount,
            tx.Balance,
            tx.Category,
            tx.IsAiProcessed
        });
    }

    public async Task<int> InsertTransactionsBatchAsync(IEnumerable<Transaction> transactions, int userId)
    {
        var list = transactions as IList<Transaction> ?? transactions.ToList();
        if (list.Count == 0) return 0;

        // Build parallel arrays and let unnest() expand them in a single INSERT. Dapper doesn't
        // expand .NET array parameters into VALUES tuples natively, but Npgsql happily binds
        // T[] -> Postgres array, so unnest() is the cleanest portable shape. ON CONFLICT keeps
        // re-runs idempotent — exactly the same semantics as the per-row sibling above.
        var ids          = list.Select(t => t.Id).ToArray();
        var accountIds   = list.Select(t => t.AccountId ?? "").ToArray();
        var dates        = list.Select(t => t.TransactionDate.UtcDateTime).ToArray();
        var descriptions = list.Select(t => t.Description ?? "").ToArray();
        var amounts      = list.Select(t => t.Amount).ToArray();
        var balances     = list.Select(t => t.Balance).ToArray();
        var categories   = list.Select(t => t.Category ?? "").ToArray();
        var processed    = list.Select(t => t.IsAiProcessed).ToArray();

        await using var connection = new NpgsqlConnection(_connectionString);
        return await connection.ExecuteAsync(@"
            INSERT INTO transactions
                (id, user_id, account_id, transaction_date, description, amount, balance, category, is_ai_processed, notes)
            SELECT u.id, @UserId, u.account_id, u.transaction_date, u.description, u.amount, u.balance, u.category, u.is_ai_processed, NULL
            FROM unnest(
                @Ids::uuid[],
                @AccountIds::text[],
                @Dates::timestamptz[],
                @Descriptions::text[],
                @Amounts::numeric[],
                @Balances::numeric[],
                @Categories::text[],
                @Processed::boolean[]
            ) AS u(id, account_id, transaction_date, description, amount, balance, category, is_ai_processed)
            ON CONFLICT (id, transaction_date, user_id) DO NOTHING;",
            new
            {
                UserId = userId,
                Ids = ids,
                AccountIds = accountIds,
                Dates = dates,
                Descriptions = descriptions,
                Amounts = amounts,
                Balances = balances,
                Categories = categories,
                Processed = processed
            });
    }

    public async Task<List<Transaction>> GetUnprocessedTransactionsAsync(int userId, int limit)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        var sql = "SELECT * FROM transactions WHERE user_id = @UserId AND (is_ai_processed = FALSE OR category IS NULL OR category = 'General' OR category = 'Undetermined') LIMIT @Limit";
        return (await connection.QueryAsync<Transaction>(sql, new { UserId = userId, Limit = limit })).ToList();
    }

    public async Task<List<Transaction>> GetTransactionsForCategorizationAsync(int userId)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        var sql = "SELECT * FROM transactions WHERE user_id = @UserId AND (is_ai_processed = FALSE OR category IS NULL OR category = 'General') LIMIT 100";
        return (await connection.QueryAsync<Transaction>(sql, new { UserId = userId })).ToList();
    }

    public async Task DeleteTransactionsByUserAsync(int userId)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.ExecuteAsync("DELETE FROM transactions WHERE user_id = @userId", new { userId });
    }

    public async Task DeleteChatHistoryAsync(int userId)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.ExecuteAsync(
            "DELETE FROM chat_history WHERE user_id = @UserId",
            new { UserId = userId });
    }

    public async Task<int> InsertChatHistoryAsync(int userId, string messageText, bool isUser)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        return await connection.ExecuteAsync(
            "INSERT INTO chat_history (user_id, message_text, is_user) VALUES (@UserId, @MessageText, @IsUser)",
            new { UserId = userId, MessageText = messageText, IsUser = isUser });
    }

    public async Task<IEnumerable<(string MessageText, bool IsUser)>> GetRecentChatHistoryAsync(int userId, int limit = 10)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        // COALESCE guards against legacy rows where is_user / message_text were inserted as NULL.
        // Previously we cast a null dynamic value straight to bool, which threw
        // "Cannot convert null to 'bool'" and broke the entire Telegram chat flow for the user.
        var res = await connection.QueryAsync<dynamic>(
            @"SELECT COALESCE(message_text, '') AS ""MessageText"",
                     COALESCE(is_user, FALSE)  AS ""IsUser""
              FROM chat_history
              WHERE user_id = @UserId
              ORDER BY timestamp DESC
              LIMIT @Limit",
            new { UserId = userId, Limit = limit });

        return res.Select(r => ((string)(r.MessageText ?? string.Empty), r.IsUser is bool b && b));
    }

    public async Task<List<Transaction>> GetHistoryForAnalysisAsync(int userId, int days)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        var cutoff = DateTime.UtcNow.AddDays(-days);
        return (await connection.QueryAsync<Transaction>(
            "SELECT * FROM transactions WHERE user_id = @UserId AND transaction_date >= @Cutoff ORDER BY transaction_date DESC",
            new { UserId = userId, Cutoff = cutoff })).ToList();
    }

    public async Task<List<(string Description, string Category)>> GetCategorizationExamplesAsync(int userId, int limit = 30)
    {
        // Pull one representative transaction per distinct category from the user's most recent
        // AI-processed history. This gives the categoriser a personalised few-shot set, so
        // "Woolworths" is learned as Groceries for this user even if it's Shopping for another.
        await using var connection = new NpgsqlConnection(_connectionString);
        const string sql = @"
            SELECT DISTINCT ON (LOWER(description)) description AS Description, category AS Category
            FROM transactions
            WHERE user_id = @UserId
              AND is_ai_processed = TRUE
              AND category IS NOT NULL
              AND category <> ''
              AND category <> 'General'
              AND category <> 'Undetermined'
              AND description IS NOT NULL
              AND description <> ''
            ORDER BY LOWER(description), transaction_date DESC
            LIMIT @Limit";
        var rows = await connection.QueryAsync<(string Description, string Category)>(sql,
            new { UserId = userId, Limit = limit });
        return rows.ToList();
    }

    public async Task<IEnumerable<dynamic>> GetChartDataAsync(int userId, string sql)
    {
        // Validate AI-generated SQL: must be a single SELECT statement, no DML/DDL,
        // and must scope results to the calling user via the @userId parameter.
        var trimmed = sql.Trim().TrimEnd(';').Trim();
        if (!trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only SELECT queries are permitted.");

        // Reject multi-statement payloads — a stray semicolon mid-query would survive the TrimEnd above.
        if (trimmed.Contains(';'))
            throw new InvalidOperationException("Multiple SQL statements are not permitted.");

        // Strip SQL comments before keyword scanning so attackers can't hide INSERT/**/INTO etc.
        var stripped = StripSqlComments(trimmed);
        var upperSql = stripped.ToUpperInvariant();

        string[] forbidden = { "INSERT", "UPDATE", "DELETE", "DROP", "ALTER", "TRUNCATE", "CREATE", "GRANT", "REVOKE", "EXEC", "EXECUTE", "INTO", "MERGE", "COPY", "CALL", "VACUUM", "ANALYZE", "LISTEN", "NOTIFY", "PG_SLEEP" };
        foreach (var keyword in forbidden)
        {
            // Word-boundary match so column names like "created_at" don't trip on CREATE.
            if (System.Text.RegularExpressions.Regex.IsMatch(upperSql, $@"\b{keyword}\b"))
                throw new InvalidOperationException($"Forbidden SQL keyword detected: {keyword}");
        }

        // Defence in depth: the query MUST reference both the user_id column and the @userId
        // parameter so that results are constrained to the calling user. Without this an AI-
        // generated query could leak data across tenants.
        if (!upperSql.Contains("USER_ID"))
            throw new InvalidOperationException("Query must filter on the user_id column.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(stripped, @"@userId\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            throw new InvalidOperationException("Query must reference the @userId parameter.");

        await using var connection = new NpgsqlConnection(_connectionString);
        // Execute in a read-only transaction for defence in depth
        await connection.OpenAsync();
        await using var txn = await connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
        await connection.ExecuteAsync("SET TRANSACTION READ ONLY", transaction: txn);
        var result = await connection.QueryAsync<dynamic>(sql, new { userId }, transaction: txn);
        await txn.CommitAsync();
        return result;
    }

    private static string StripSqlComments(string sql)
    {
        // Remove /* ... */ block comments (non-greedy) and -- line comments.
        var noBlock = System.Text.RegularExpressions.Regex.Replace(sql, @"/\*[\s\S]*?\*/", " ");
        var noLine = System.Text.RegularExpressions.Regex.Replace(noBlock, @"--[^\r\n]*", " ");
        return noLine;
    }

    public async Task UpdateTransactionNoteAsync(Guid transactionId, string note)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.ExecuteAsync(
            "UPDATE transactions SET notes = @Note WHERE id = @Id",
            new { Note = note, Id = transactionId });
    }
}
