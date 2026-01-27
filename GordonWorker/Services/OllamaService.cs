using System.Text;
using System.Text.Json;

namespace GordonWorker.Services;

public interface IOllamaService
{
    Task<string> GenerateSqlAsync(string userPrompt);
    Task<string> FormatResponseAsync(string userPrompt, string dataContext);
    Task<string> GenerateSimpleReportAsync(string statsJson);
    Task<bool> TestConnectionAsync();
}

public class OllamaService : IOllamaService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OllamaService> _logger;
    private readonly ISettingsService _settingsService;

    public OllamaService(HttpClient httpClient, ILogger<OllamaService> logger, IConfiguration configuration, ISettingsService settingsService)
    {
        _httpClient = httpClient;
        _logger = logger;
        _settingsService = settingsService;
    }

    private async Task<(string Url, string Model)> GetConnectionDetailsAsync()
    {
        var settings = await _settingsService.GetSettingsAsync();
        return (settings.OllamaBaseUrl, settings.OllamaModelName);
    }

    public async Task<bool> TestConnectionAsync()
    {
        try
        {
            var (baseUrl, _) = await GetConnectionDetailsAsync();
            var baseUri = baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/";
            // /api/tags lists models, lightweight check
            var fullUrl = new Uri(new Uri(baseUri), "api/tags"); 

            var response = await _httpClient.GetAsync(fullUrl);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ollama connection test failed.");
            return false;
        }
    }

    public async Task<string> GenerateSqlAsync(string userPrompt)
    {
        var systemPrompt = @"You are a PostgreSQL expert. 
The table schema is:
transactions (
    id UUID PRIMARY KEY,
    transaction_date TIMESTAMPTZ,
    description TEXT,
    amount DECIMAL(18, 2),
    balance DECIMAL(18, 2),
    category TEXT,
    is_ai_processed BOOLEAN
)
Generate a single valid PostgreSQL SELECT query to answer the user's question. 
Return ONLY the SQL query. Do not include markdown formatting or explanations.";

        return await GenerateCompletionAsync(systemPrompt, userPrompt);
    }

    public async Task<string> FormatResponseAsync(string userPrompt, string dataContext)
    {
        var settings = await _settingsService.GetSettingsAsync();
        var persona = settings.SystemPersona;

        var systemPrompt = $@"You are a Senior Financial Analyst and Actuary named '{persona}'. 
Your goal is to provide expert financial advice based on the provided Data Context.
The Data Context may contain a JSON 'FinancialHealthReport' with fields like:
- WeightedDailyBurn (EMA)
- BurnVolatility (Standard Deviation of spend)
- ValueAtRisk95 (Max probable daily spend)
- SafeRunwayDays (Conservative estimate)
- OptimisticRunwayDays (Best case)

Interpret these metrics for the user. 
- If 'SafeRunwayDays' is low, warn them. 
- Use 'ValueAtRisk95' to explain potential daily shocks. 
- Mention the 'TrendDirection'.

If the Data Context is just a list of transactions, summarize it.
Keep the tone professional, concise, and use English UK. 
Do not explicitly mention 'JSON' or 'fields', just speak naturally about the figures.";

        var fullPrompt = $"Question: {userPrompt}\nData Context: {dataContext}";
        return await GenerateCompletionAsync(systemPrompt, fullPrompt);
    }

    public async Task<string> GenerateSimpleReportAsync(string statsJson)
    {
        var systemPrompt = @"You are a friendly financial assistant explaining finances to a non-technical user.
Use the provided JSON statistics to write a weekly summary.

**Guidelines:**
- **Currency:** ALWAYS use South African Rands (ZAR) symbol 'R'.
- **Tone:** Encouraging, clear, and simple (Junior High level).
- **Format:** 
  - Use short sentences.
  - Use HTML bullet points (<ul><li>) for lists.
  - Do NOT use Markdown (no **bold**, no # headers), use HTML tags like <b> or <h3>.
  
**Content to Cover:**
- **Runway:** Explain it as ""How many days until the money runs out if you keep spending like this"".
- **Volatility:** Explain it as ""How stable your spending is"".
- **Comparison:** Compare 'SpendThisWeek' vs 'SpendLastWeek'.

Make it look like a clean, professional email body.";

        return await GenerateCompletionAsync(systemPrompt, $"Here are the stats: {statsJson}");
    }

    private async Task<string> GenerateCompletionAsync(string system, string prompt)
    {
        var (baseUrl, model) = await GetConnectionDetailsAsync();
        var request = new
        {
            model = model,
            prompt = $"{system}\n\n{prompt}",
            stream = false
        };

        var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
        
        try 
        {
            // Ensure trailing slash logic
            var baseUri = baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/";
            var fullUrl = new Uri(new Uri(baseUri), "api/generate");

            var response = await _httpClient.PostAsync(fullUrl, content);
            response.EnsureSuccessStatusCode();

            var responseString = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<OllamaResponse>(responseString);
            return result?.Response?.Trim() ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling Ollama at {Url}.", baseUrl);
            return "I'm sorry, I couldn't process that request right now.";
        }
    }

    private class OllamaResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("response")]
        public string? Response { get; set; }
    }
}