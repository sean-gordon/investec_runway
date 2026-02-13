using Dapper;
using Npgsql;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace GordonWorker.Services;

public class AppSettings
{
    public string UserName { get; set; } = "Sir/Madam"; // Default name
    public string ReportDayOfWeek { get; set; } = "Monday";
    public int ReportHour { get; set; } = 9;
    public decimal ActuarialAlpha { get; set; } = 0.15m;
    public int AnalysisWindowDays { get; set; } = 90;
    public decimal UnexpectedPaymentThreshold { get; set; } = 3000m;
    public decimal IncomeAlertThreshold { get; set; } = 5000m; // Alert when income > this
    public string OllamaBaseUrl { get; set; } = "http://host.docker.internal:11434";
    public string OllamaModelName { get; set; } = "deepseek-coder";
    public string GeminiApiKey { get; set; } = "";
    public string AiProvider { get; set; } = "Ollama"; // "Ollama" or "Gemini"
    public string SystemPersona { get; set; } = "Gordon";
    
    // Actuarial Keywords & Thresholds
    public string FixedCostKeywords { get; set; } = "SCHOOL,MORTGAGE,LEVIES,HOME LOAN,INSURANCE,BOND,INVESTMENT,LIFE,MEDICAL,NEDBHL,DISC PREM,WILLOWBROOKE,ADAM";
    public string SalaryKeywords { get; set; } = "TCP 131,TCP131,SALARY";
    public decimal SalaryFallbackThreshold { get; set; } = 10000m;
    public int SalaryFallbackDays { get; set; } = 45;
    public decimal StabilityPercentageThreshold { get; set; } = 15m;
    public decimal StabilityAmountThreshold { get; set; } = 250m;
    public decimal TrendSensitivity { get; set; } = 0.1m; // 10% deviation for trend change
    public string InvestecBaseUrl { get; set; } = "https://openapi.investec.com/";
    public string InvestecClientId { get; set; } = "";
    public string InvestecSecret { get; set; } = "";
    public string InvestecApiKey { get; set; } = "";
    public double SyncBufferDays { get; set; } = 0.05; // ~1 hour

    // Data Settings
    public int HistoryDaysBack { get; set; } = 180;
    public string CurrencyCulture { get; set; } = "en-ZA"; // For formatting (e.g. en-ZA, en-US, en-GB)

    // Email Settings
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 587;
    public string SmtpUser { get; set; } = "";
    public string SmtpPass { get; set; } = "";
    public string EmailTo { get; set; } = "";

    // Twilio WhatsApp Settings
    public string TwilioAccountSid { get; set; } = "";
    public string TwilioAuthToken { get; set; } = "";
    public string TwilioWhatsAppNumber { get; set; } = "";
    public string AuthorizedWhatsAppNumber { get; set; } = ""; // To restrict who can chat

    // Telegram Settings
    public string TelegramBotToken { get; set; } = "";
    public string TelegramChatId { get; set; } = "";
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
    private readonly IDataProtector _protector;
    private AppSettings? _cachedSettings;

    public SettingsService(IConfiguration configuration, ILogger<SettingsService> logger, IDataProtectionProvider dataProtectionProvider)
    {
        _configuration = configuration;
        _logger = logger;
        _protector = dataProtectionProvider.CreateProtector("GordonFinanceEngine.Settings.v1");
    }

    public async Task<AppSettings> GetSettingsAsync()
    {
        if (_cachedSettings != null) return _cachedSettings;

        try
        {
            using var connection = new NpgsqlConnection(_configuration.GetConnectionString("DefaultConnection"));
            await connection.OpenAsync();

            var json = await connection.QuerySingleOrDefaultAsync<string>("SELECT config FROM system_config WHERE id = 1");

            AppSettings settings;
            if (string.IsNullOrEmpty(json))
            {
                _logger.LogInformation("No settings found in database. Initialising defaults.");
                settings = new AppSettings();
                // Try to save defaults, but don't crash if DB is unreachable during startup
                try { await SaveToDbAsync(settings); } 
                catch (Exception ex) { _logger.LogWarning(ex, "Could not save default settings to DB. Using in-memory defaults."); }
            }
            else
            {
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                settings = JsonSerializer.Deserialize<AppSettings>(json, options) ?? new AppSettings();
                
                // Decrypt sensitive fields
                settings.GeminiApiKey = TryDecrypt(settings.GeminiApiKey);
                settings.InvestecSecret = TryDecrypt(settings.InvestecSecret);
                settings.InvestecApiKey = TryDecrypt(settings.InvestecApiKey);
                settings.SmtpPass = TryDecrypt(settings.SmtpPass);
                settings.TwilioAuthToken = TryDecrypt(settings.TwilioAuthToken);
                settings.InvestecClientId = TryDecrypt(settings.InvestecClientId);
                settings.TwilioAccountSid = TryDecrypt(settings.TwilioAccountSid);
                settings.TelegramBotToken = TryDecrypt(settings.TelegramBotToken);
            }

            _cachedSettings = settings;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CRITICAL: Failed to load settings from DB.");
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

            // Create a clone to encrypt without affecting the in-memory settings
            var encryptedSettings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
            
            encryptedSettings.GeminiApiKey = TryEncrypt(settings.GeminiApiKey);
            encryptedSettings.InvestecSecret = TryEncrypt(settings.InvestecSecret);
            encryptedSettings.InvestecApiKey = TryEncrypt(settings.InvestecApiKey);
            encryptedSettings.SmtpPass = TryEncrypt(settings.SmtpPass);
            encryptedSettings.TwilioAuthToken = TryEncrypt(settings.TwilioAuthToken);
            encryptedSettings.InvestecClientId = TryEncrypt(settings.InvestecClientId);
            encryptedSettings.TwilioAccountSid = TryEncrypt(settings.TwilioAccountSid);
            encryptedSettings.TelegramBotToken = TryEncrypt(settings.TelegramBotToken);

            var json = JsonSerializer.Serialize(encryptedSettings);
            
            var sql = @"
                INSERT INTO system_config (id, config) 
                VALUES (1, @Config::jsonb) 
                ON CONFLICT (id) DO UPDATE SET config = @Config::jsonb";

            await connection.ExecuteAsync(sql, new { Config = json });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CRITICAL: Failed to save settings to DB.");
            throw;
        }
    }

    private string TryEncrypt(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return "";
        try { return _protector.Protect(plainText); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to encrypt setting.");
            return plainText; 
        }
    }

    private string TryDecrypt(string cipherText)
    {
        if (string.IsNullOrEmpty(cipherText)) return "";
        try { return _protector.Unprotect(cipherText); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to decrypt setting. This is expected if the key was recently added or is plain text.");
            return cipherText; 
        }
    }
}