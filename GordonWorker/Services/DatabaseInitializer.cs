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

            // 1. Users Table
            var usersSql = @"
                CREATE TABLE IF NOT EXISTS users (
                    id SERIAL PRIMARY KEY,
                    username TEXT UNIQUE NOT NULL,
                    password_hash TEXT NOT NULL,
                    role TEXT DEFAULT 'User',
                    is_system BOOLEAN DEFAULT FALSE,
                    created_at TIMESTAMPTZ DEFAULT NOW()
                );";
            await connection.ExecuteAsync(usersSql);

            // Ensure columns exist (migration for existing DB)
            try { await connection.ExecuteAsync("ALTER TABLE users ADD COLUMN IF NOT EXISTS role TEXT DEFAULT 'User';"); } catch {}
            try { await connection.ExecuteAsync("ALTER TABLE users ADD COLUMN IF NOT EXISTS is_system BOOLEAN DEFAULT FALSE;"); } catch {}
            try { await connection.ExecuteAsync("ALTER TABLE users ADD COLUMN IF NOT EXISTS last_weekly_report_sent TIMESTAMPTZ;"); } catch {}

            // Seed System Admin
            var adminUser = _configuration["ADMIN_USERNAME"] ?? "admin";
            var adminPass = _configuration["ADMIN_PASSWORD"];
            
            if (string.IsNullOrEmpty(adminPass))
            {
                // SECURITY FIX: Do not use weak default "admin123".
                // If not provided in ENV, we generate a random one and log it once.
                _logger.LogWarning("ADMIN_PASSWORD not set in environment. Generating a secure one...");
                adminPass = Guid.NewGuid().ToString("N").Substring(0, 12);
                _logger.LogCritical("******************************************************************");
                _logger.LogCritical($" INITIAL ADMIN PASSWORD: {adminPass} ");
                _logger.LogCritical(" PLEASE RECORD THIS AND CHANGE IT IN THE UI IMMEDIATELY. ");
                _logger.LogCritical("******************************************************************");
            }

            var adminHash = BCrypt.Net.BCrypt.HashPassword(adminPass);

            var upsertAdminSql = @"
                INSERT INTO users (username, password_hash, role, is_system) 
                VALUES (@Username, @PasswordHash, 'Admin', TRUE)
                ON CONFLICT (username) DO UPDATE 
                SET password_hash = @PasswordHash, role = 'Admin', is_system = TRUE 
                WHERE users.is_system = TRUE OR users.username = @Username
                RETURNING id;";

            var adminId = await connection.QuerySingleAsync<int>(upsertAdminSql, new { Username = adminUser, PasswordHash = adminHash });
            _logger.LogInformation("System Admin user ensured ('{User}', ID: {Id}).", adminUser, adminId);

            // 2. User Settings Table
            var settingsSql = @"
                CREATE TABLE IF NOT EXISTS user_settings (
                    user_id INT PRIMARY KEY REFERENCES users(id) ON DELETE CASCADE,
                    config JSONB NOT NULL
                );";
            await connection.ExecuteAsync(settingsSql);

            // 2.1 Chat History Table
            var chatHistorySql = @"
                CREATE TABLE IF NOT EXISTS chat_history (
                    id SERIAL PRIMARY KEY,
                    user_id INT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                    message_text TEXT NOT NULL,
                    is_user BOOLEAN NOT NULL,
                    timestamp TIMESTAMPTZ DEFAULT NOW()
                );
                CREATE INDEX IF NOT EXISTS idx_chat_history_user_date ON chat_history(user_id, timestamp DESC);";
            await connection.ExecuteAsync(chatHistorySql);

            // 3. Transactions Table Migration
            // Ensure Transactions Table exists (for new installs)
            var transactionsSql = @"
                CREATE TABLE IF NOT EXISTS transactions (
                    id UUID NOT NULL,
                    user_id INT NOT NULL,
                    account_id TEXT,
                    transaction_date TIMESTAMPTZ NOT NULL,
                    description TEXT,
                    amount DECIMAL(18, 2),
                    balance DECIMAL(18, 2),
                    category TEXT,
                    is_ai_processed BOOLEAN DEFAULT FALSE,
                    notes TEXT
                );";
            await connection.ExecuteAsync(transactionsSql);

            // Add user_id if missing (migration)
            // Use a more robust check for the column
            var colCheckSql = "SELECT COUNT(*) FROM information_schema.columns WHERE table_name = 'transactions' AND column_name = 'user_id'";
            var hasUserId = await connection.ExecuteScalarAsync<int>(colCheckSql) > 0;
            if (!hasUserId)
            {
                _logger.LogInformation("Adding 'user_id' column to 'transactions' table...");
                try {
                    await connection.ExecuteAsync("ALTER TABLE transactions ADD COLUMN user_id INT;");
                    await connection.ExecuteAsync("UPDATE transactions SET user_id = @AdminId WHERE user_id IS NULL", new { AdminId = adminId });
                    await connection.ExecuteAsync("ALTER TABLE transactions ALTER COLUMN user_id SET NOT NULL;");
                } catch (Exception ex) {
                    _logger.LogError(ex, "Failed to add 'user_id' column. It might already exist but check failed.");
                }
            }

            // Migration for notes
            try { await connection.ExecuteAsync("ALTER TABLE transactions ADD COLUMN IF NOT EXISTS notes TEXT;"); } catch {}

            // 4. Index Migration
            // TimescaleDB hypertables require unique indexes to include the partitioning column (transaction_date)
            try 
            {
                _logger.LogInformation("Ensuring correct unique index for transactions...");
                // Drop legacy indexes/constraints that might conflict with the new multi-tenant structure
                await connection.ExecuteAsync("DROP INDEX IF EXISTS ux_transactions_id_date;");
                await connection.ExecuteAsync("DROP INDEX IF EXISTS ux_transactions_id;");
                
                // IMPORTANT: Drop the actual PRIMARY KEY constraint if it exists, as it often only covers (id) or (id, date) 
                // but lacks user_id, or prevents index creation.
                var dropPkeySql = @"
                    DO $$ 
                    BEGIN 
                        IF EXISTS (SELECT 1 FROM information_schema.table_constraints WHERE constraint_name = 'transactions_pkey' AND table_name = 'transactions') THEN
                            ALTER TABLE transactions DROP CONSTRAINT transactions_pkey;
                        END IF;
                    END $$;";
                await connection.ExecuteAsync(dropPkeySql);
                
                // Create the definitive unique index required for Sync (ON CONFLICT target)
                // In TimescaleDB, the partitioning column (transaction_date) MUST be part of any unique index.
                await connection.ExecuteAsync("CREATE UNIQUE INDEX IF NOT EXISTS ux_transactions_id_date_user ON transactions (id, transaction_date, user_id);"); 
                _logger.LogInformation("Unique index 'ux_transactions_id_date_user' ensured.");
            } 
            catch (Exception ex) 
            { 
                _logger.LogError(ex, "CRITICAL: Could not ensure unique index 'ux_transactions_id_date_user'. Sync will fail."); 
            }

            // Convert to Hypertable
            try 
            { 
                // Note: migrate_data => true is required if data already exists
                await connection.ExecuteAsync("SELECT create_hypertable('transactions', 'transaction_date', if_not_exists => TRUE, migrate_data => TRUE);"); 
            } 
            catch (Exception ex)
            {
                // If it's already a hypertable, this will fail but we can ignore it
                if (!ex.Message.Contains("already")) _logger.LogWarning("Hypertable conversion note: {Message}", ex.Message);
            }

            // Cleanup old config table
            try { await connection.ExecuteAsync("DROP TABLE IF EXISTS system_config;"); } catch {}

            // 5. Historic Data Migration: Fix Transaction Polarity
            try
            {
                _logger.LogInformation("Ensuring historic transactions have correct polarity...");
                var fixPolaritySql = @"
                    UPDATE transactions SET amount = ABS(amount) WHERE category = 'CREDIT';
                    UPDATE transactions SET amount = -ABS(amount) WHERE category = 'DEBIT';
                ";
                var rowsAffected = await connection.ExecuteAsync(fixPolaritySql);
                if (rowsAffected > 0)
                {
                    _logger.LogInformation("Corrected polarity for {Count} historic transactions.", rowsAffected);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to run transaction polarity migration.");
            }

            _logger.LogInformation("Database multi-tenant schema ensured.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize database.");
            throw; // Rethrow to stop startup if DB is critical fail
        }
    }
}
