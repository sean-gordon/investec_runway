using Dapper;
using Npgsql;

namespace GordonWorker.Services;

public class DatabaseInitializer
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<DatabaseInitializer> _logger;

    public DatabaseInitializer(IConfiguration configuration, ILogger<DatabaseInitializer> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task InitializeAsync()
    {
        try
        {
            var connectionString = _configuration.GetConnectionString("DefaultConnection");
            using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();

            var sql = @"
                CREATE TABLE IF NOT EXISTS system_config (
                    id INT PRIMARY KEY DEFAULT 1 CHECK (id = 1),
                    config JSONB NOT NULL
                );";

            await connection.ExecuteAsync(sql);

            // Ensure transactions table exists with correct schema for TimescaleDB
            var transactionsSql = @"
                CREATE TABLE IF NOT EXISTS transactions (
                    id UUID NOT NULL,
                    account_id TEXT,
                    transaction_date TIMESTAMPTZ NOT NULL,
                    description TEXT,
                    amount DECIMAL(18, 2),
                    balance DECIMAL(18, 2),
                    category TEXT,
                    is_ai_processed BOOLEAN DEFAULT FALSE,
                    PRIMARY KEY (id, transaction_date)
                );
                
                -- TimescaleDB hypertables need a unique index that includes the partitioning column
                CREATE UNIQUE INDEX IF NOT EXISTS ux_transactions_id_date ON transactions (id, transaction_date);
            ";

            await connection.ExecuteAsync(transactionsSql);
            
            // Try to convert to hypertable (fails silently if already a hypertable)
            try { await connection.ExecuteAsync("SELECT create_hypertable('transactions', 'transaction_date', if_not_exists => TRUE);"); } catch {}

            _logger.LogInformation("Database tables and indexes ensured.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize database tables.");
        }
    }
}
