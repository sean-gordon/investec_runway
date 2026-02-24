using Dapper;
using Npgsql;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Memory;

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
    public string GeminiModelName { get; set; } = "gemini-3-flash-preview";
    public string GeminiApiKey { get; set; } = "";
    public string AiProvider { get; set; } = "Ollama"; // "Ollama" or "Gemini"
    public string SystemPersona { get; set; } = "Gordon";

    // AI Fallback Settings
    public bool EnableAiFallback { get; set; } = true;
    public string FallbackAiProvider { get; set; } = "Gemini"; // Fallback if primary fails
    public string FallbackOllamaBaseUrl { get; set; } = "http://host.docker.internal:11434";
    public string FallbackOllamaModelName { get; set; } = "llama3";
    public string FallbackGeminiModelName { get; set; } = "gemini-3-flash-preview";
    public string FallbackGeminiApiKey { get; set; } = "";
    public int AiTimeoutSeconds { get; set; } = 90;
    public int AiRetryAttempts { get; set; } = 2;

    // Thinking Model Settings
    public bool EnableThinkingModel { get; set; } = false;
    public string ThinkingAiProvider { get; set; } = "Ollama";
    public string ThinkingOllamaBaseUrl { get; set; } = "http://host.docker.internal:11434";
    public string ThinkingOllamaModelName { get; set; } = "deepseek-r1";
    public string ThinkingGeminiModelName { get; set; } = "gemini-2.0-flash-thinking-exp";
    public string ThinkingGeminiApiKey { get; set; } = "";
    
    // Actuarial Keywords & Thresholds
    public string FixedCostKeywords { get; set; } = "SCHOOL,MORTGAGE,LEVIES,HOME LOAN,INSURANCE,BOND,INVESTMENT,LIFE,MEDICAL,NEDBHL,DISC PREM,WILLOWBROOKE,ADAM,NETFLIX,SPOTIFY,APPLE,GOOGLE,VODACOM,MTN,CELL C,TELKOM,ELECTRICITY,CITY OF,MUNICIPALITY,DISCOVERY,MULTICHOICE,DSTV,VUMATEL,AFRIHOST,MWEB,RAIN,OUTSURANCE,SANTAM,OLD MUTUAL,SANLAM,LIBERTY,ALLAN GRAY,CORONATION,RETIREMENT,PENSION,STALMENT,BOND,FIBRE,GYM,VIRGIN ACTIVE,PLANET FITNESS,AUDIBLE,AMAZON,CHATGPT,OPENAI";
    public string SalaryKeywords { get; set; } = "TCP 131,TCP131,SALARY";
    public decimal SalaryFallbackThreshold { get; set; } = 10000m;
    public int SalaryFallbackDays { get; set; } = 45;
    public decimal StabilityPercentageThreshold { get; set; } = 15m;
    public decimal StabilityAmountThreshold { get; set; } = 250m;
    public decimal TrendSensitivity { get; set; } = 0.1m; // 10% deviation for trend change
    
    // Advanced Actuarial
    public decimal PulseBaselineThreshold { get; set; } = 0.1m;
    public decimal HybridBaselineThreshold { get; set; } = 0.1m;
    public int MinCycleDays { get; set; } = 20;
    public int MaxCycleDays { get; set; } = 45;
    public int DefaultCycleDays { get; set; } = 30;
    public double VarConfidenceInterval { get; set; } = 1.645;

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
    public string TelegramAuthorizedChatIds { get; set; } = "";
}

public interface ISettingsService
{
    Task<AppSettings> GetSettingsAsync(int userId);
    Task UpdateSettingsAsync(int userId, AppSettings newSettings);
    void InvalidateCache(int userId);
}

public class SettingsService : ISettingsService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<SettingsService> _logger;
    private readonly IDataProtector _protector;
    private readonly IMemoryCache _cache;

    public SettingsService(IConfiguration configuration, ILogger<SettingsService> logger, IDataProtectionProvider dataProtectionProvider, IMemoryCache cache)
    {
        _configuration = configuration;
        _logger = logger;
        _protector = dataProtectionProvider.CreateProtector("GordonFinanceEngine.Settings.v1");
        _cache = cache;
    }

    public void InvalidateCache(int userId)
    {
        _cache.Remove($"settings_{userId}");
        _logger.LogInformation("Settings cache invalidated for user {UserId}", userId);
    }

    public async Task<AppSettings> GetSettingsAsync(int userId)
    {
        return await _cache.GetOrCreateAsync($"settings_{userId}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            entry.SlidingExpiration = TimeSpan.FromMinutes(2);

            try
            {
                using var connection = new NpgsqlConnection(_configuration.GetConnectionString("DefaultConnection"));
                await connection.OpenAsync();

                var json = await connection.QuerySingleOrDefaultAsync<string>(
                    "SELECT config FROM user_settings WHERE user_id = @userId", new { userId });

                AppSettings settings;
                if (string.IsNullOrEmpty(json))
                {
                    _logger.LogInformation("No settings found for user {UserId}. Initialising defaults.", userId);
                    settings = new AppSettings();
                    await SaveToDbAsync(userId, settings);
                }
                else
                {
                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    settings = JsonSerializer.Deserialize<AppSettings>(json, options) ?? new AppSettings();

                    // Decrypt sensitive fields
                    settings.GeminiApiKey = TryDecrypt(settings.GeminiApiKey);
                    settings.FallbackGeminiApiKey = TryDecrypt(settings.FallbackGeminiApiKey);
                    settings.ThinkingGeminiApiKey = TryDecrypt(settings.ThinkingGeminiApiKey);
                    settings.InvestecSecret = TryDecrypt(settings.InvestecSecret);
                    settings.InvestecApiKey = TryDecrypt(settings.InvestecApiKey);
                    settings.SmtpPass = TryDecrypt(settings.SmtpPass);
                    settings.TwilioAuthToken = TryDecrypt(settings.TwilioAuthToken);
                    settings.InvestecClientId = TryDecrypt(settings.InvestecClientId);
                    settings.TwilioAccountSid = TryDecrypt(settings.TwilioAccountSid);
                    settings.TelegramBotToken = TryDecrypt(settings.TelegramBotToken);
                }

                return settings;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load settings for user {UserId}", userId);
                return new AppSettings();
            }
        }) ?? new AppSettings();
    }

    public async Task UpdateSettingsAsync(int userId, AppSettings newSettings)
    {
        await SaveToDbAsync(userId, newSettings);
        InvalidateCache(userId);
    }

    private async Task SaveToDbAsync(int userId, AppSettings settings)
    {
        try
        {
            using var connection = new NpgsqlConnection(_configuration.GetConnectionString("DefaultConnection"));
            await connection.OpenAsync();

            // Create a clone to encrypt without affecting the in-memory settings
            var encryptedSettings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
            
            encryptedSettings.GeminiApiKey = TryEncrypt(settings.GeminiApiKey);
            encryptedSettings.FallbackGeminiApiKey = TryEncrypt(settings.FallbackGeminiApiKey);
            encryptedSettings.ThinkingGeminiApiKey = TryEncrypt(settings.ThinkingGeminiApiKey);
            encryptedSettings.InvestecSecret = TryEncrypt(settings.InvestecSecret);
            encryptedSettings.InvestecApiKey = TryEncrypt(settings.InvestecApiKey);
            encryptedSettings.SmtpPass = TryEncrypt(settings.SmtpPass);
            encryptedSettings.TwilioAuthToken = TryEncrypt(settings.TwilioAuthToken);
            encryptedSettings.InvestecClientId = TryEncrypt(settings.InvestecClientId);
            encryptedSettings.TwilioAccountSid = TryEncrypt(settings.TwilioAccountSid);
            encryptedSettings.TelegramBotToken = TryEncrypt(settings.TelegramBotToken);

            var json = JsonSerializer.Serialize(encryptedSettings);
            
            var sql = @"
                INSERT INTO user_settings (user_id, config) 
                VALUES (@userId, @Config::jsonb) 
                ON CONFLICT (user_id) DO UPDATE SET config = @Config::jsonb";

            await connection.ExecuteAsync(sql, new { userId, Config = json });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save settings for user {UserId}", userId);
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
            _logger.LogWarning(ex, "Failed to decrypt setting.");
            return cipherText; 
        }
    }
}
