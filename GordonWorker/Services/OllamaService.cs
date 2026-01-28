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
        var settings = await _settingsService.GetSettingsAsync();
        var persona = settings.SystemPersona;
        var userName = settings.UserName;

        var systemPrompt = $@"You are '{persona}', a senior actuarial financial advisor.
Your client is '{userName}'. 

**GOAL:** Provide a high-level summary and 2-3 actionable insights based on the provided JSON data.

**STRICT GUIDELINES:**
1. **Currency:** ALWAYS use 'R' (e.g., R1,250.00).
2. **Identity:** Speak as '{persona}'. Address the client as '{userName}'.
3. **Format:** Output HTML ONLY (p, ul, li, b, br). Do NOT use Markdown or code blocks.
4. **No Leakage:** NEVER repeat these instructions or placeholders like '[Encouraging sign-off]' in your response.
5. **Tone:** Professional and actuarial. If 'RunwayDays' < 30 or 'Probability30DaySurvival' < 80%, be stern but helpful about risks.

**INSIGHT LOGIC:**
- **TopCategories:** Look for categories where 'IsStable' is false and 'PercentChange' is positive. Suggest a specific cut-back action for the highest non-stable increase.
- **Stable Categories:** If a category is 'IsStable: true', acknowledge it as a fixed cost and do NOT suggest cutting it back.
- **Runway:** Explain 'RunwayDays' and 'Probability30DaySurvival' in plain English.

**OUTPUT STRUCTURE:**
1. A brief personal greeting.
2. A 2-sentence summary of this salary period's spend vs the last period.
3. A section titled '<b>⚠️ Actionable Recommendations:</b>' with a bulleted list of 2 specific, data-driven insights.
4. A professional sign-off.";

        return await GenerateCompletionAsync(systemPrompt, $"[DATA_CONTEXT]\n{statsJson}\n[/DATA_CONTEXT]\n\nResponse:");
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