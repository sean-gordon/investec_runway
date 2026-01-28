using System.Text;
using System.Text.Json;

namespace GordonWorker.Services;

public interface IAiService
{
    Task<string> GenerateSqlAsync(string userPrompt);
    Task<string> FormatResponseAsync(string userPrompt, string dataContext);
    Task<string> GenerateSimpleReportAsync(string statsJson);
    Task<(bool Success, string Error)> TestConnectionAsync();
    Task<List<string>> GetAvailableModelsAsync();
}

public class AiService : IAiService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<AiService> _logger;
    private readonly ISettingsService _settingsService;

    public AiService(HttpClient httpClient, ILogger<AiService> logger, IConfiguration configuration, ISettingsService settingsService)
    {
        _httpClient = httpClient;
        _logger = logger;
        _settingsService = settingsService;
    }

    private async Task<(string Provider, string OllamaUrl, string OllamaModel, string GeminiKey)> GetConnectionDetailsAsync()
    {
        var settings = await _settingsService.GetSettingsAsync();
        return (settings.AiProvider, settings.OllamaBaseUrl, settings.OllamaModelName, settings.GeminiApiKey);
    }

    public async Task<List<string>> GetAvailableModelsAsync()
    {
        var (provider, baseUrl, _, _) = await GetConnectionDetailsAsync();
        if (provider == "Gemini")
        {
            return new List<string> 
            { 
                "gemini-2.0-flash", 
                "gemini-1.5-pro", 
                "gemini-1.5-flash",
                "gemini-1.5-flash-8b"
            };
        }

        try
        {
            var baseUri = baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/";
            var fullUrl = new Uri(new Uri(baseUri), "api/tags");
            var response = await _httpClient.GetAsync(fullUrl);
            if (!response.IsSuccessStatusCode) return new List<string>();
            var responseString = await response.Content.ReadAsStringAsync();
            var tagsResponse = JsonSerializer.Deserialize<OllamaTagsResponse>(responseString);
            return tagsResponse?.Models?.Select(m => m.Name).Where(n => !string.IsNullOrEmpty(n)).Cast<string>().ToList() ?? new List<string>();
        }
        catch (Exception ex) { _logger.LogError(ex, "Failed to fetch models from Ollama."); return new List<string>(); }
    }

    public async Task<(bool Success, string Error)> TestConnectionAsync()
    {
        var (provider, baseUrl, model, geminiKey) = await GetConnectionDetailsAsync();
        try
        {
            if (provider == "Gemini")
            {
                if (string.IsNullOrWhiteSpace(geminiKey)) return (false, "Gemini API Key is missing.");
                var result = await GenerateGeminiCompletionAsync("System", "Say 'OK'", geminiKey);
                if (string.IsNullOrWhiteSpace(result) || result.Contains("Error:")) return (false, result ?? "Empty response.");
                return (true, string.Empty);
            }
            var baseUri = baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/";
            var fullUrl = new Uri(new Uri(baseUri), "api/generate");
            var request = new { model = model, prompt = "Say 'OK'", stream = false };
            var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(fullUrl, content);
            if (!response.IsSuccessStatusCode) return (false, $"Ollama error ({response.StatusCode})");
            return (true, string.Empty);
        }
        catch (Exception ex) { _logger.LogError(ex, "AI Connection test failed."); return (false, ex.Message); }
    }

    public async Task<string> GenerateSqlAsync(string userPrompt)
    {
        var systemPrompt = "You are a PostgreSQL expert. Return ONLY the SQL query.";
        return await GenerateCompletionAsync(systemPrompt, userPrompt);
    }

    public async Task<string> FormatResponseAsync(string userPrompt, string dataContext)
    {
        var settings = await _settingsService.GetSettingsAsync();
        var systemPrompt = $"You are a senior financial analyst named {settings.SystemPersona}.";
        return await GenerateCompletionAsync(systemPrompt, userPrompt + "\nContext: " + dataContext);
    }

    public async Task<string> GenerateSimpleReportAsync(string statsJson)
    {
        var settings = await _settingsService.GetSettingsAsync();
        var persona = settings.SystemPersona;
        var userName = settings.UserName;
        var systemPrompt = $"You are {persona}, a senior actuarial financial advisor for {userName}. STRICT GUIDELINES: 1. Currency: ALWAYS use R symbol. 2. Fixed Costs: NEVER suggest cut-backs for School, Mortgage, Levies, Home Loan, or Insurance. 3. Output HTML ONLY (p, ul, li, b). INSIGHT LOGIC: Focus on ProjectedBalanceAtPayday. If low, warn. Only suggest cut-backs for non-stable categories that are NOT fixed costs. STRUCTURE: 1. Greeting. 2. Summary including projected payday balance. 3. 'Actionable Recommendations' section with bullet points. 4. Sign-off.";
        return await GenerateCompletionAsync(systemPrompt, $"[DATA_CONTEXT]\n{statsJson}\n[/DATA_CONTEXT]\n\nResponse:");
    }

    private async Task<string> GenerateCompletionAsync(string system, string prompt)
    {
        var (provider, _, _, geminiKey) = await GetConnectionDetailsAsync();
        if (provider == "Gemini") return await GenerateGeminiCompletionAsync(system, prompt, geminiKey);
        
        var settings = await _settingsService.GetSettingsAsync();
        var model = settings.OllamaModelName;
        var baseUrl = settings.OllamaBaseUrl;
        var request = new { model = model, prompt = $"{system}\n\n{prompt}", stream = false };
        var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
        try 
        {
            var baseUri = baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/";
            var fullUrl = new Uri(new Uri(baseUri), "api/generate");
            var response = await _httpClient.PostAsync(fullUrl, content);
            response.EnsureSuccessStatusCode();
            var responseString = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<OllamaResponse>(responseString);
            return result?.Response?.Trim() ?? string.Empty;
        }
        catch (Exception ex) { _logger.LogError(ex, "Error calling Ollama."); return "I'm sorry, I couldn't process that request right now."; }
    }

    private async Task<string> GenerateGeminiCompletionAsync(string system, string prompt, string apiKey)
    {
        try
        {
            var settings = await _settingsService.GetSettingsAsync();
            var model = !string.IsNullOrWhiteSpace(settings.OllamaModelName) ? settings.OllamaModelName : "gemini-1.5-flash";
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";
            var request = new
            {
                contents = new[] { new { role = "user", parts = new[] { new { text = system + "\n\n" + prompt } } } },
                safetySettings = new[]
                {
                    new { category = "HARM_CATEGORY_HARASSMENT", threshold = "BLOCK_NONE" },
                    new { category = "HARM_CATEGORY_HATE_SPEECH", threshold = "BLOCK_NONE" },
                    new { category = "HARM_CATEGORY_SEXUALLY_EXPLICIT", threshold = "BLOCK_NONE" },
                    new { category = "HARM_CATEGORY_DANGEROUS_CONTENT", threshold = "BLOCK_NONE" }
                }
            };
            var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(url, content);
            if (!response.IsSuccessStatusCode) { var errorBody = await response.Content.ReadAsStringAsync(); return $"Error: Gemini API returned {response.StatusCode}. Details: {errorBody}"; }
            var responseString = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(responseString);
            if (doc.RootElement.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
            {
                var firstCandidate = candidates[0];
                if (firstCandidate.TryGetProperty("content", out var candidateContent) && candidateContent.TryGetProperty("parts", out var parts) && parts.GetArrayLength() > 0)
                {
                    return parts[0].GetProperty("text").GetString()?.Trim() ?? "";
                }
            }
            return "Error: Gemini returned an empty response candidate.";
        }
        catch (Exception ex) { _logger.LogError(ex, "Gemini API call failed."); return "I'm sorry, I couldn't process that request via Gemini."; }
    }

    private class OllamaResponse { [System.Text.Json.Serialization.JsonPropertyName("response")] public string? Response { get; set; } }
    private class OllamaTagsResponse { [System.Text.Json.Serialization.JsonPropertyName("models")] public List<OllamaModelTag>? Models { get; set; } }
    private class OllamaModelTag { [System.Text.Json.Serialization.JsonPropertyName("name")] public string? Name { get; set; } }
}
