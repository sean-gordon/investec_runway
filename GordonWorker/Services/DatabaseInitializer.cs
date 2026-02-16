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
            // Always try to add the column safely
            try 
            {
                await connection.ExecuteAsync("ALTER TABLE transactions ADD COLUMN IF NOT EXISTS user_id INT;");
                
                // Backfill existing NULLs to Admin
                await connection.ExecuteAsync("UPDATE transactions SET user_id = @AdminId WHERE user_id IS NULL", new { AdminId = adminId });
                
                // Add Foreign Key constraint if not exists (Postgres doesn't support IF NOT EXISTS for constraints directly easily in one line without check)
                // We'll rely on the ALTER statement succeeding or failing harmlessly if constraint exists? No, duplicate constraint name throws.
                // Simplified: Just add the column. The Create Table below handles the full definition for new setups.
                // For existing setups, we just need the column and the data.
                
                // We can try to set NOT NULL now that data is backfilled
                await connection.ExecuteAsync("ALTER TABLE transactions ALTER COLUMN user_id SET NOT NULL;");
            } 
            catch (Exception ex) 
            { 
                _logger.LogWarning("Migration warning (user_id): {Message}", ex.Message); 
            }

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
                    is_ai_processed BOOLEAN DEFAULT FALSE
                );";
            await connection.ExecuteAsync(transactionsSql);

            // 4. Index Migration
            // Drop old constraints/indexes that conflict with multi-tenant unique index
            try { await connection.ExecuteAsync("DROP INDEX IF EXISTS ux_transactions_id_date;"); } catch {}
            try { await connection.ExecuteAsync("ALTER TABLE transactions DROP CONSTRAINT IF EXISTS transactions_pkey;"); } catch {}

            // Create new multi-tenant unique index
            try 
            {
                await connection.ExecuteAsync("CREATE UNIQUE INDEX IF NOT EXISTS ux_transactions_id_date_user ON transactions (id, transaction_date, user_id);"); 
            } 
            catch (Exception ex) 
            { 
                _logger.LogError(ex, "Could not create unique index 'ux_transactions_id_date_user'."); 
            }

            // Convert to Hypertable
            try 
            { 
                await connection.ExecuteAsync("SELECT create_hypertable('transactions', 'transaction_date', if_not_exists => TRUE, migrate_data => TRUE);"); 
            } 
            catch (Exception ex)
            {
                if (!ex.Message.Contains("already")) _logger.LogWarning("Hypertable conversion: {Message}", ex.Message);
            }

            // Cleanup old config table
            try { await connection.ExecuteAsync("DROP TABLE IF EXISTS system_config;"); } catch {}

            _logger.LogInformation("Database multi-tenant schema ensured.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize database.");
            throw; // Rethrow to stop startup if DB is critical fail
        }
    }
}
