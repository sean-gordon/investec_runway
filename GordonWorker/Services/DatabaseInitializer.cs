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
            _logger.LogInformation("Database system_config table ensured.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize database tables.");
        }
    }
}
