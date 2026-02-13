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

            // Seed System Admin
            var adminUser = _configuration["ADMIN_USERNAME"] ?? "admin";
            var adminPass = _configuration["ADMIN_PASSWORD"] ?? "admin123";
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

            // 3. Transactions Table Migration
            // Check if user_id column exists
            var checkColumnSql = "SELECT column_name FROM information_schema.columns WHERE table_name='transactions' AND column_name='user_id'";
            var colExists = await connection.ExecuteScalarAsync<string>(checkColumnSql);

            if (colExists == null)
            {
                _logger.LogInformation("Migrating transactions table to multi-tenant...");
                
                // Add column (nullable first to allow backfill)
                await connection.ExecuteAsync("ALTER TABLE transactions ADD COLUMN IF NOT EXISTS user_id INT REFERENCES users(id) ON DELETE CASCADE;");
                
                // Backfill existing transactions to Admin user (User 1 or whatever ID we got)
                await connection.ExecuteAsync("UPDATE transactions SET user_id = @AdminId WHERE user_id IS NULL", new { AdminId = adminId });
                
                // Make not null
                await connection.ExecuteAsync("ALTER TABLE transactions ALTER COLUMN user_id SET NOT NULL;");
                
                // Drop old primary key constraint if it exists (might fail if hypertable restrictions apply, but we try)
                try { await connection.ExecuteAsync("ALTER TABLE transactions DROP CONSTRAINT transactions_pkey;"); } catch {}
                
                // Drop old index if exists
                try { await connection.ExecuteAsync("DROP INDEX IF EXISTS transactions_id_transaction_date_idx;"); } catch {}
                try { await connection.ExecuteAsync("DROP INDEX IF EXISTS ux_transactions_id_date;"); } catch {} // Drop previous unique index
            }

            // Ensure Schema
            var transactionsSql = @"
                CREATE TABLE IF NOT EXISTS transactions (
                    id UUID NOT NULL,
                    user_id INT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
                    account_id TEXT,
                    transaction_date TIMESTAMPTZ NOT NULL,
                    description TEXT,
                    amount DECIMAL(18, 2),
                    balance DECIMAL(18, 2),
                    category TEXT,
                    is_ai_processed BOOLEAN DEFAULT FALSE
                    -- TimescaleDB usually manages PKs differently, we rely on unique index
                );";
            await connection.ExecuteAsync(transactionsSql);

            // Ensure Unique Index for Hypertable (must include time column)
            // AND for our app logic (must include user_id and id)
            // Composite: id, transaction_date, user_id
            try 
            {
                await connection.ExecuteAsync("CREATE UNIQUE INDEX IF NOT EXISTS ux_transactions_id_date_user ON transactions (id, transaction_date, user_id);"); 
            } 
            catch (Exception ex) 
            { 
                _logger.LogWarning(ex, "Could not create unique index. This might be due to duplicate data during migration."); 
            }

            // Convert to Hypertable if not already
            try 
            { 
                await connection.ExecuteAsync("SELECT create_hypertable('transactions', 'transaction_date', if_not_exists => TRUE, migrate_data => TRUE);"); 
            } 
            catch (Exception ex)
            {
                // Ignore "already a hypertable" errors, log others
                if (!ex.Message.Contains("already")) _logger.LogWarning(ex, "Hypertable conversion warning.");
            }

            // Cleanup old table
            try { await connection.ExecuteAsync("DROP TABLE IF EXISTS system_config;"); } catch {}

            _logger.LogInformation("Database multi-tenant schema ensured.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize multi-tenant database tables.");
        }
    }
}
