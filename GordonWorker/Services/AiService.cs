using System.Text;
using System.Text.Json;

namespace GordonWorker.Services;

public interface IAiService
{
    Task<string> GenerateSqlAsync(int userId, string userPrompt);
    Task<string> FormatResponseAsync(int userId, string userPrompt, string dataContext, bool isWhatsApp = false);
    Task<string> GenerateSimpleReportAsync(int userId, string statsJson);
    Task<(bool Success, string Error)> TestConnectionAsync(int userId);
    Task<List<string>> GetAvailableModelsAsync(int userId);
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

    private async Task<(string Provider, string OllamaUrl, string OllamaModel, string GeminiKey)> GetConnectionDetailsAsync(int userId)
    {
        var settings = await _settingsService.GetSettingsAsync(userId);
        return (settings.AiProvider, settings.OllamaBaseUrl, settings.OllamaModelName, settings.GeminiApiKey);
    }

    public async Task<List<string>> GetAvailableModelsAsync(int userId)
    {
        var (provider, baseUrl, _, geminiKey) = await GetConnectionDetailsAsync(userId);
        
        if (provider == "Gemini")
        {
            if (string.IsNullOrWhiteSpace(geminiKey)) return new List<string>();
            try
            {
                var url = $"https://generativelanguage.googleapis.com/v1beta/models?key={geminiKey}";
                var response = await _httpClient.GetAsync(url);
                if (!response.IsSuccessStatusCode) return new List<string> { "gemini-1.5-flash", "gemini-1.5-pro" };
                
                var responseString = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(responseString);
                var modelNames = new List<string>();
                
                if (doc.RootElement.TryGetProperty("models", out var models))
                {
                    foreach (var m in models.EnumerateArray())
                    {
                        var name = m.GetProperty("name").GetString() ?? "";
                        if (name.StartsWith("models/")) name = name.Substring(7);
                        if (name.Contains("gemini") && !name.Contains("vision") && !name.Contains("embedding"))
                        {
                            modelNames.Add(name);
                        }
                    }
                }
                return modelNames.OrderByDescending(n => n).ToList();
            }
            catch { return new List<string> { "gemini-1.5-flash", "gemini-1.5-pro" }; }
        }

        if (string.IsNullOrWhiteSpace(baseUrl)) return new List<string>();

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

    public async Task<(bool Success, string Error)> TestConnectionAsync(int userId)
    {
        var (provider, baseUrl, model, geminiKey) = await GetConnectionDetailsAsync(userId);
        try
        {
            if (provider == "Gemini")
            {
                if (string.IsNullOrWhiteSpace(geminiKey)) return (false, "Gemini API Key is missing.");
                var result = await GenerateGeminiCompletionAsync(userId, "System", "Say 'OK'", geminiKey);
                if (string.IsNullOrWhiteSpace(result) || result.Contains("Error:")) return (false, result ?? "Empty response.");
                return (true, string.Empty);
            }

            if (string.IsNullOrWhiteSpace(baseUrl)) return (false, "Ollama URL is not configured.");
            if (string.IsNullOrWhiteSpace(model)) return (false, "Please select a model first.");

            var baseUri = baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/";
            var fullUrl = new Uri(new Uri(baseUri), "api/generate");
            var request = new { model = model, prompt = "Say 'OK'", stream = false };
            var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
            
            _logger.LogInformation("Testing Ollama connection: {Url}, Model: {Model}", fullUrl, model);
            
            var response = await _httpClient.PostAsync(fullUrl, content);
            
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return (false, $"Model '{model}' not found on Ollama server. Have you run 'ollama pull {model}'?");
            }

            if (!response.IsSuccessStatusCode) return (false, $"Ollama error ({response.StatusCode})");
            return (true, string.Empty);
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("refused") || ex.Message.Contains("known"))
        {
            return (false, $"Could not reach Ollama at {baseUrl}. Check the URL and ensure OLLAMA_HOST=0.0.0.0 is set.");
        }
        catch (Exception ex) { _logger.LogError(ex, "AI Connection test failed."); return (false, ex.Message); }
    }

    public async Task<string> GenerateSqlAsync(int userId, string userPrompt)
    {
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        var systemPrompt = $@"You are a PostgreSQL expert for a financial database.
Current Date: {today}

Table 'transactions' schema:
- transaction_date (timestamptz)
- description (text)
- amount (numeric): IMPORTANT - POSITIVE numbers are Expenses (Debits), NEGATIVE numbers are Income/Credits (Deposits).
- balance (numeric)
- category (text)

Return ONLY the raw SQL query. Do NOT use Markdown formatting (no ```sql). Do NOT include explanations.";
        return await GenerateCompletionAsync(userId, systemPrompt, userPrompt);
    }

    public async Task<string> FormatResponseAsync(int userId, string userPrompt, string dataContext, bool isWhatsApp = false)
    {
        var settings = await _settingsService.GetSettingsAsync(userId);
        var persona = settings.SystemPersona;
        var userName = settings.UserName;

        var formattingRule = isWhatsApp 
            ? "4. **Formatting:** Use WhatsApp formatting: *bold* for bold, _italics_ for italics, and - for bullet points. Do NOT use HTML or standard Markdown bold (**)."
            : "4. **Formatting:** Use standard Markdown (bold, lists) for readability. Do NOT use HTML.";

        var systemPrompt = $@"You are {persona}, a senior actuarial financial advisor for {userName}.
        
**STRICT GUIDELINES:**
1. **Currency:** ALWAYS use R symbol for ZAR (e.g. R1,500.00). 
2. **Sign Convention:** In this database, POSITIVE numbers are Expenses (Debits) and NEGATIVE numbers are Income/Credits (Deposits).
3. **Math Accuracy:** If you are provided with an Actuarial Report or Summary, PRIORITIZE those calculated values over doing your own math on raw transactions.
{formattingRule}
5. **Tone:** Professional, concise, and helpful.

Context Information:
{dataContext}";

        return await GenerateCompletionAsync(userId, systemPrompt, userPrompt);
    }

    // New overload for chat controller convenience, assuming userId is handled by controller context if needed, 
    // but ChatController calls GenerateSimpleReportAsync(string message). 
    // Actually, ChatController logic needs to pass userId. 
    // I'll update ChatController to use the (int userId, string statsJson) signature if possible, or overload here.
    // Wait, the ChatController calls `GenerateSimpleReportAsync(request.Message)`. That method didn't exist in my previous write.
    // I'll implement `GenerateSimpleReportAsync(string message)` as a bridge that might fail if userId isn't context aware, 
    // but better yet, I'll remove it and force ChatController to use `FormatResponseAsync` or similar.
    // Actually, `GenerateSimpleReportAsync` was used for the WEEKLY report logic.
    // Let's implement the one for the weekly report properly:

    public async Task<string> GenerateSimpleReportAsync(int userId, string statsJson)
    {
        var settings = await _settingsService.GetSettingsAsync(userId);
        var persona = settings.SystemPersona;
        var userName = settings.UserName;
        
        var systemPrompt = $@"You are {persona}, a senior actuarial financial advisor for {userName}. 
    
    **STRICT GUIDELINES:**
    1. **Currency:** ALWAYS use R symbol (e.g. R1,500.00).
    2. **Fixed Costs:** NEVER suggest cut-backs for School, Mortgage, Levies, Home Loan, or Insurance. Treat these as essential overhead.
    3. **No Hallucination:** Only use provided numbers. Do not invent reasons for fluctuations.
    4. **Format:** Output HTML ONLY (p, ul, li, b). Do NOT use Markdown.
    
    **INSIGHT LOGIC:**
    - **Balance Projection:** Mention the 'ProjectedBalanceAtPayday'. This ALREADY accounts for both predicted daily burn AND the 'UpcomingFixedCosts' (unpaid large bills).
    - **Upcoming Payments:** Acknowledge any bills in 'UpcomingFixedCosts' as pending liabilities.
    - **TopCategories:** ONLY suggest cut-backs for 'TopCategoriesWithIncreases' (categories with non-stable spending growth).
    - **Runway:** Explain 'RunwayDays' and 'ProbabilityToReachPayday'.
    
    **OUTPUT STRUCTURE:**
    1. A personal greeting.
    2. A summary of current spend vs last period, explicitly mentioning the projected balance before next payday.
    3. A section titled '<b>⚠️ Actionable Recommendations:</b>' with a bulleted list. 
       - If 'UpcomingFixedCosts' has items, remind the user to ensure funds are ready for them.
       - If 'AllTopCategoriesAreStable' is true, provide 1-2 generic tips for long-term wealth building or automating savings.
       - Otherwise, suggest specific cut-backs for categories in 'TopCategoriesWithIncreases'.
       - ALWAYS ensure the list contains at least one item.
    4. A professional sign-off.";

        return await GenerateCompletionAsync(userId, systemPrompt, $"[DATA_CONTEXT]\n{statsJson}\n[/DATA_CONTEXT]\n\nResponse:");
    }

    // Overload for ChatController - wait, ChatController calls this with just a message string. 
    // It should probably call FormatResponseAsync instead for chat. 
    // I will add this overload but it will just wrap FormatResponseAsync for now to satisfy the interface if needed,
    // BUT since I am refactoring, I will NOT include the invalid overload and instead fix ChatController.
    // Wait, the interface definition above has `Task<string> GenerateSimpleReportAsync(string message);`
    // I should implement it but it lacks userId. 
    // BETTER PLAN: Remove that overload from interface and fix ChatController to call `FormatResponseAsync(userId, msg, "")`.
    
    public async Task<string> GenerateSimpleReportAsync(string message) 
    {
        // This is a dummy implementation to satisfy the interface if I keep it, 
        // but it's dangerous because it doesn't know the user.
        // I will throw an exception to force me to fix the caller.
        throw new NotImplementedException("Use the userId overload.");
    }

    private async Task<string> GenerateCompletionAsync(int userId, string system, string prompt, CancellationToken ct = default)
    {
        var (provider, baseUrl, model, geminiKey) = await GetConnectionDetailsAsync(userId);
        
        if (provider == "Gemini") return await GenerateGeminiCompletionAsync(userId, system, prompt, geminiKey, ct); 
        
        if (string.IsNullOrWhiteSpace(model))
        {
            _logger.LogWarning("AI model name is not configured for user {UserId}.", userId);
            return "I'm sorry, I don't have a model selected. Please check your settings.";
        }

        var request = new { model = model, prompt = $"{system}\n\n{prompt}", stream = false };
        var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
        try 
        {
            var baseUri = baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/";
            var fullUrl = new Uri(new Uri(baseUri), "api/generate");
            
            _logger.LogInformation("Sending request to Ollama: {Url}, Model: {Model}", fullUrl, model);
            
            var response = await _httpClient.PostAsync(fullUrl, content, ct);
            
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return $"Error: The AI model '{model}' was not found on your Ollama server.";
            }

            response.EnsureSuccessStatusCode();
            var responseString = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<OllamaResponse>(responseString);
            return result?.Response?.Trim() ?? string.Empty;
        }
        catch (Exception ex) { _logger.LogError(ex, "Error calling Ollama at {Url}", baseUrl); return "I'm sorry, I couldn't process that request right now."; }
    }

    private async Task<string> GenerateGeminiCompletionAsync(int userId, string system, string prompt, string apiKey, CancellationToken ct = default)
    {
        try
        {
            var settings = await _settingsService.GetSettingsAsync(userId);
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
            var response = await _httpClient.PostAsync(url, content, ct);
            if (!response.IsSuccessStatusCode) { var errorBody = await response.Content.ReadAsStringAsync(ct); return $"Error: Gemini API returned {response.StatusCode}."; }
            var responseString = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(responseString);
            if (doc.RootElement.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
            {
                return candidates[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString()?.Trim() ?? "";
            }
            return "Error: Gemini returned an empty response candidate.";
        }
        catch (Exception ex) { _logger.LogError(ex, "Gemini API call failed."); return "I'm sorry, I couldn't process that request via Gemini."; }
    }

    private class OllamaResponse { [System.Text.Json.Serialization.JsonPropertyName("response")] public string? Response { get; set; } }
    private class OllamaTagsResponse { [System.Text.Json.Serialization.JsonPropertyName("models")] public List<OllamaModelTag>? Models { get; set; } }
    private class OllamaModelTag { [System.Text.Json.Serialization.JsonPropertyName("name")] public string? Name { get; set; } }
}
