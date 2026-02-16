using System.Text;
using System.Text.Json;
using GordonWorker.Models;

namespace GordonWorker.Services;

public interface IAiService
{
    Task<string> GenerateSqlAsync(int userId, string userPrompt);
    Task<string> FormatResponseAsync(int userId, string userPrompt, string dataContext, bool isWhatsApp = false);
    Task<string> GenerateSimpleReportAsync(int userId, string statsJson);
    Task<(Guid? TransactionId, string? Note)> AnalyzeExpenseExplanationAsync(int userId, string userMessage, List<Transaction> recentTransactions);
    Task<(bool IsAffordabilityCheck, decimal? Amount, string? Description)> AnalyzeAffordabilityAsync(int userId, string userMessage);
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

    public async Task<(bool IsAffordabilityCheck, decimal? Amount, string? Description)> AnalyzeAffordabilityAsync(int userId, string userMessage)
    {
        var systemPrompt = @"You are a financial intent analyzer.
YOUR GOAL: Detect if the user is asking if they can afford something.

EXAMPLES:
- 'Can I buy a new TV for R5000?' -> { ""isCheck"": true, ""amount"": 5000, ""desc"": ""New TV"" }
- 'Can I afford a holiday?' -> { ""isCheck"": true, ""amount"": null, ""desc"": ""Holiday"" }
- 'Do I have enough for dinner?' -> { ""isCheck"": true, ""amount"": null, ""desc"": ""Dinner"" }
- 'What is my balance?' -> { ""isCheck"": false }

OUTPUT FORMAT:
JSON ONLY: { ""isCheck"": boolean, ""amount"": number_or_null, ""desc"": string_or_null }";

        var jsonResponse = await GenerateCompletionAsync(userId, systemPrompt, $"USER MESSAGE: \"{userMessage}\"");

        try
        {
            var match = System.Text.RegularExpressions.Regex.Match(jsonResponse, @"```json\s*(.*?)\s*```", System.Text.RegularExpressions.RegexOptions.Singleline);
            var cleanJson = match.Success ? match.Groups[1].Value : jsonResponse.Trim();

            using var doc = JsonDocument.Parse(cleanJson);
            var root = doc.RootElement;
            
            if (root.TryGetProperty("isCheck", out var isCheckEl) && isCheckEl.GetBoolean())
            {
                decimal? amount = null;
                if (root.TryGetProperty("amount", out var amountEl) && amountEl.ValueKind == JsonValueKind.Number)
                    amount = amountEl.GetDecimal();
                
                var desc = root.TryGetProperty("desc", out var descEl) ? descEl.GetString() : null;
                
                return (true, amount, desc);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to parse affordability analysis: {Message}", ex.Message);
        }

        return (false, null, null);
    }

    public async Task<(Guid? TransactionId, string? Note)> AnalyzeExpenseExplanationAsync(int userId, string userMessage, List<Transaction> recentTransactions)
    {
        var settings = await _settingsService.GetSettingsAsync(userId);
        
        // Filter for transactions that likely need explanation (high value or income)
        var candidates = recentTransactions
            .Where(t => Math.Abs(t.Amount) > 500) // Optimization: only look at significant ones
            .OrderByDescending(t => t.TransactionDate)
            .Take(10)
            .Select(t => new { t.Id, t.Description, Amount = t.Amount, Date = t.TransactionDate.ToString("yyyy-MM-dd") })
            .ToList();

        if (!candidates.Any()) return (null, null);

        var candidatesJson = JsonSerializer.Serialize(candidates);
        var systemPrompt = @"You are a financial data assistant. Your ONLY job is to link a user's explanation to a specific transaction.

INPUT DATA:
1. User Message
2. List of Recent Transactions (JSON)

logic:
- If the user's message clearly explains what a specific transaction was for (e.g., 'The 5000 was for a laptop', 'Woolworths was groceries'), identify the Transaction ID.
- The Note should be the user's explanation cleaned up (e.g., 'Laptop purchase', 'Groceries').
- If the user is just saying 'hello' or asking a general question, return NULL.

OUTPUT FORMAT:
Return ONLY a JSON object: { ""id"": ""GUID"", ""note"": ""..."" } or { ""id"": null }";

        var prompt = $"USER MESSAGE: \"{userMessage}\"\n\nTRANSACTIONS:\n{candidatesJson}";
        
        var jsonResponse = await GenerateCompletionAsync(userId, systemPrompt, prompt);
        
        try 
        {
            var match = System.Text.RegularExpressions.Regex.Match(jsonResponse, @"```json\s*(.*?)\s*```", System.Text.RegularExpressions.RegexOptions.Singleline);
            var cleanJson = match.Success ? match.Groups[1].Value : jsonResponse.Trim();

            using var doc = JsonDocument.Parse(cleanJson);
            if (doc.RootElement.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String)
            {
                var idStr = idElement.GetString();
                if (Guid.TryParse(idStr, out var guid))
                {
                    var note = doc.RootElement.GetProperty("note").GetString();
                    return (guid, note);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to parse AI explanation analysis: {Message}", ex.Message);
        }

        return (null, null);
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

        var systemPrompt = $@"You are {persona}, a distinguished Personal Chief Financial Officer and Actuary for {userName}.
        
**YOUR ROLE:**
You have direct access to the client's transaction ledger. Your goal is to provide high-level strategic financial counsel. You are not a chatbot; you are a serious professional partner in their wealth-building journey.

**TONE & STYLE:**
- **Formal & Professional:** Use precise financial terminology (e.g., 'liquidity', 'burn rate', 'capital allocation').
- **Strategic:** Don't just report numbers; explain their implications. Look for patterns.
- **Direct & Uncompromising:** If spending is unsustainable, say so clearly but respectfully. 
- **Helpful:** Your ultimate goal is to help the client master their cash flow.

**DATA CONTEXT:**
The user has provided a JSON summary of their current financial health. 
- **Notes:** The user may have provided specific explanations for certain transactions (e.g. 'That was a gift'). Use these notes to provide more accurate and personal advice.
- **Projected Balance:** This is the most critical metric. Focus on it.
- **Runway:** This is their safety net.

**GUIDELINES:**
1. **Currency:** ALWAYS use the R symbol (e.g., R1,500.00).
2. **Context:** If the user asks a specific question, answer it directly using the data. If they just say 'hello', provide a brief executive summary.
3. **Accuracy:** Do not invent transactions. Stick to the provided summary stats.

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
        
        var systemPrompt = $@"You are {persona}, serving as the Chief Financial Officer for {userName}'s personal estate.
    
    **OBJECTIVE:**
    Compose a formal Weekly Financial Briefing for the principal. This document should read like a boardroom executive summary, not a generic automated email.

    **ANALYSIS PROTOCOL:**
    1. **Liquidity Assessment:** Evaluate the 'ProjectedBalanceAtPayday'. Is the principal on track to solvency, or is a capital injection (or spending freeze) required?
    2. **Liability Management:** Review 'UpcomingFixedCosts'. Confirm that sufficient liquidity exists to cover these obligations.
    3. **Variance Analysis:** Scrutinize 'TopCategoriesWithIncreases'. If variable spending is trending upward, identify the root cause (the specific category) and recommend a course correction.
    4. **Risk Profile:** Comment on the 'RunwayDays' and 'ProbabilityToReachPayday'. Frame this in terms of financial security.

    **OUTPUT FORMAT:**
    - Use purely semantic HTML tags (p, ul, li, b).
    - **Tone:** authoritative, strategic, and highly professional.
    - **Structure:**
        - **Executive Summary:** A 2-sentence overview of the current position.
        - **Strategic Recommendations:** A bulleted list of 2-3 specific, high-impact actions the principal should take immediately.
    
    **CONSTRAINTS:**
    - Do not suggest cutting fixed costs (Mortgages, Insurance) unless the situation is critical.
    - Focus on discretionary spend control.
    - Use the R symbol for currency.";

        return await GenerateCompletionAsync(userId, systemPrompt, $"[DATA_CONTEXT]\n{statsJson}\n[/DATA_CONTEXT]\n\nResponse:");
    }

    // Overload for ChatController - wait, ChatController calls this with just a message string. 
    // It should probably call FormatResponseAsync instead for chat. 
    // I will add this overload but it will just wrap FormatResponseAsync for now to satisfy the interface if needed,
    // BUT since I am refactoring, I will NOT include the invalid overload and instead fix ChatController.
    // Wait, the interface definition above has `Task<string> GenerateSimpleReportAsync(string message);`
    // I should implement it but it lacks userId. 
    // BETTER PLAN: Remove that overload from interface and fix ChatController to call `FormatResponseAsync(userId, msg, "")`.
    
    public Task<string> GenerateSimpleReportAsync(string message) 
    {
        // This is a dummy implementation to satisfy the interface if I keep it, 
        // but it's dangerous because it doesn't know the user.
        // I will throw an exception to force me to fix the caller.
        return Task.FromException<string>(new NotImplementedException("Use the userId overload."));
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
