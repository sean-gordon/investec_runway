using Dapper;
using Npgsql;
using System.Text.Json;

namespace GordonWorker.Services;

public class AppSettings
{
    public string UserName { get; set; } = "Sir/Madam"; // Default name
    public string ReportDayOfWeek { get; set; } = "Monday";
    public int ReportHour { get; set; } = 9;
    public decimal ActuarialAlpha { get; set; } = 0.15m;
    public int AnalysisWindowDays { get; set; } = 90;
    public decimal UnexpectedPaymentThreshold { get; set; } = 3000m;
    public string OllamaBaseUrl { get; set; } = "http://host.docker.internal:11434";
    public string OllamaModelName { get; set; } = "deepseek-coder";
    public string GeminiApiKey { get; set; } = "";
    public string AiProvider { get; set; } = "Ollama"; // "Ollama" or "Gemini"
    public string SystemPersona { get; set; } = "Gordon";
    
    // Data Settings
    public int HistoryDaysBack { get; set; } = 180;
    public string CurrencyCulture { get; set; } = "en-ZA"; // For formatting (e.g. en-ZA, en-US, en-GB)

    // Email Settings
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 587;
    public string SmtpUser { get; set; } = "";
    public string SmtpPass { get; set; } = "";
    public string EmailTo { get; set; } = "";
}

public interface ISettingsService
{
    Task<AppSettings> GetSettingsAsync();
    Task UpdateSettingsAsync(AppSettings newSettings);
}

public class SettingsService : ISettingsService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<SettingsService> _logger;
    private AppSettings? _cachedSettings;

    public SettingsService(IConfiguration configuration, ILogger<SettingsService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<AppSettings> GetSettingsAsync()
    {
        if (_cachedSettings != null) return _cachedSettings;

        try
        {
            using var connection = new NpgsqlConnection(_configuration.GetConnectionString("DefaultConnection"));
            await connection.OpenAsync();

            var json = await connection.QuerySingleOrDefaultAsync<string>("SELECT config FROM system_config WHERE id = 1");

            if (string.IsNullOrEmpty(json))
            {
                // No settings found, initialize defaults
                _cachedSettings = new AppSettings();
                await SaveToDbAsync(_cachedSettings);
            }
            else
            {
                _cachedSettings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load settings from DB. Returning defaults.");
            // Fallback to defaults if DB is unreachable (e.g. during startup)
            return new AppSettings();
        }

        return _cachedSettings;
    }

    public async Task UpdateSettingsAsync(AppSettings newSettings)
    {
        _cachedSettings = newSettings;
        await SaveToDbAsync(newSettings);
    }

    private async Task SaveToDbAsync(AppSettings settings)
    {
        try
        {
            using var connection = new NpgsqlConnection(_configuration.GetConnectionString("DefaultConnection"));
            await connection.OpenAsync();

            var json = JsonSerializer.Serialize(settings);
            
            var sql = @"
                INSERT INTO system_config (id, config) 
                VALUES (1, @Config::jsonb) 
                ON CONFLICT (id) DO UPDATE SET config = @Config::jsonb";

            await connection.ExecuteAsync(sql, new { Config = json });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save settings to DB.");
            throw;
        }
    }
}