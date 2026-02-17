using System.Text;
using System.Text.Json;
using System.Threading;
using GordonWorker.Models;

namespace GordonWorker.Services;

public interface IAiService
{
    Task<string> GenerateSqlAsync(int userId, string userPrompt);
    Task<string> FormatResponseAsync(int userId, string userPrompt, string dataContext, bool isWhatsApp = false);
    Task<string> GenerateSimpleReportAsync(int userId, string statsJson);
    Task<(Guid? TransactionId, string? Note)> AnalyzeExpenseExplanationAsync(int userId, string userMessage, List<Transaction> recentTransactions);
    Task<(bool IsAffordabilityCheck, decimal? Amount, string? Description)> AnalyzeAffordabilityAsync(int userId, string userMessage);
    Task<(bool IsChartRequest, string? ChartType, string? Sql, string? Title)> AnalyzeChartRequestAsync(int userId, string userMessage);
        Task<(bool Success, string Error)> TestConnectionAsync(int userId, bool useFallback = false);        
        Task<List<string>> GetAvailableModelsAsync(int userId, bool useFallback = false, AppSettings? overriddenSettings = null);
    }
/// <summary>
/// This is Gordon's "Brain." It handles talking to the AI—whether you're using 
/// local LLMs (Ollama) or cloud ones (Gemini). It also makes sure Gordon always 
/// responds by switching over to a backup AI automatically if the primary one is busy.
/// </summary>
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

    public async Task<(bool IsChartRequest, string? ChartType, string? Sql, string? Title)> AnalyzeChartRequestAsync(int userId, string userMessage)
    {
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        var systemPrompt = $@"You are a financial data architect.
Current Date: {today}
Table 'transactions': id (uuid), user_id (int), transaction_date (timestamptz), description (text), amount (numeric), category (text).
NOTE: Amount POSITIVE = Expense, NEGATIVE = Income.

YOUR GOAL: Detect if the user wants a chart/graph of specific data.

EXAMPLES:
- 'Show me a barchart of Uber Eats' -> {{ ""isChart"": true, ""type"": ""bar"", ""sql"": ""SELECT description as Label, SUM(amount) as Value FROM transactions WHERE user_id = @userId AND description ILIKE '%UBER EATS%' GROUP BY description"", ""title"": ""Uber Eats Spending"" }}
- 'Graph my total spending per day' -> {{ ""isChart"": true, ""type"": ""line"", ""sql"": ""SELECT transaction_date::date as Label, SUM(amount) as Value FROM transactions WHERE user_id = @userId AND amount > 0 GROUP BY 1 ORDER BY 1"", ""title"": ""Daily Spending Trend"" }}
- 'Who are my top 5 categories?' -> {{ ""isChart"": true, ""type"": ""bar"", ""sql"": ""SELECT category as Label, SUM(amount) as Value FROM transactions WHERE user_id = @userId AND amount > 0 GROUP BY 1 ORDER BY 2 DESC LIMIT 5"", ""title"": ""Top 5 Categories"" }}

OUTPUT FORMAT:
JSON ONLY: {{ ""isChart"": boolean, ""type"": ""bar|line"", ""sql"": ""..."", ""title"": ""..."" }}
If not a chart request, isChart = false. Do NOT return any other text.";

        var jsonResponse = await GenerateCompletionWithFallbackAsync(userId, systemPrompt, $"USER REQUEST: \"{userMessage}\"");

        try
        {
            var match = System.Text.RegularExpressions.Regex.Match(jsonResponse, @"```json\s*(.*?)\s*```", System.Text.RegularExpressions.RegexOptions.Singleline);
            var cleanJson = match.Success ? match.Groups[1].Value : jsonResponse.Trim();

            using var doc = JsonDocument.Parse(cleanJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("isChart", out var isChartEl) && isChartEl.GetBoolean())
            {
                return (
                    true,
                    root.GetProperty("type").GetString(),
                    root.GetProperty("sql").GetString(),
                    root.GetProperty("title").GetString()
                );
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Chart analysis parse failure. Raw: {Raw}. Error: {Msg}", jsonResponse, ex.Message);
        }

        return (false, null, null, null);
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

        var jsonResponse = await GenerateCompletionWithFallbackAsync(userId, systemPrompt, $"USER MESSAGE: \"{userMessage}\"");

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
            _logger.LogWarning("Affordability analysis parse failure. Raw: {Raw}. Error: {Msg}", jsonResponse, ex.Message);
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

        var jsonResponse = await GenerateCompletionWithFallbackAsync(userId, systemPrompt, prompt);

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
            _logger.LogWarning("Explanation analysis parse failure. Raw: {Raw}. Error: {Msg}", jsonResponse, ex.Message);
        }

        return (null, null);
    }

    private async Task<AiProviderConfig> GetProviderConfigAsync(int userId, bool useFallback = false, AppSettings? overriddenSettings = null)
    {
        var settings = overriddenSettings ?? await _settingsService.GetSettingsAsync(userId);

        if (useFallback && settings.EnableAiFallback)
        {
            _logger.LogInformation("Using fallback AI provider: {Provider}", settings.FallbackAiProvider);
            return new AiProviderConfig
            {
                Provider = settings.FallbackAiProvider,
                OllamaUrl = settings.FallbackOllamaBaseUrl,
                OllamaModel = settings.FallbackOllamaModelName,
                GeminiKey = settings.FallbackGeminiApiKey,
                TimeoutSeconds = settings.AiTimeoutSeconds
            };
        }

        return new AiProviderConfig
        {
            Provider = settings.AiProvider,
            OllamaUrl = settings.OllamaBaseUrl,
            OllamaModel = settings.OllamaModelName,
            GeminiKey = settings.GeminiApiKey,
            TimeoutSeconds = settings.AiTimeoutSeconds
        };
    }

        public async Task<List<string>> GetAvailableModelsAsync(int userId, bool useFallback = false, AppSettings? overriddenSettings = null)        
        {
            var config = await GetProviderConfigAsync(userId, useFallback, overriddenSettings);
    
            if (config.Provider == "Gemini")        {
            if (string.IsNullOrWhiteSpace(config.GeminiKey)) return new List<string>();
            try
            {
                var url = $"https://generativelanguage.googleapis.com/v1beta/models?key={config.GeminiKey}";
                var response = await _httpClient.GetAsync(url);
                if (!response.IsSuccessStatusCode) return new List<string> { "gemini-1.5-flash", "gemini-1.5-pro", "gemini-2.0-flash-exp" };

                var responseString = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(responseString);
                var modelNames = new List<string>();

                if (doc.RootElement.TryGetProperty("models", out var models))
                {
                    foreach (var m in models.EnumerateArray())
                    {
                        var name = m.GetProperty("name").GetString() ?? "";
                        // Gemini names are often "models/gemini-..."
                        if (name.Contains("/")) name = name.Split('/').Last();
                        
                        if (name.ToLower().Contains("gemini") && !name.ToLower().Contains("vision") && !name.ToLower().Contains("embedding"))
                        {
                            modelNames.Add(name);
                        }
                    }
                }
                
                if (!modelNames.Any())
                {
                    _logger.LogWarning("No suitable Gemini models found in API response. Returning defaults.");
                    return new List<string> { "gemini-1.5-flash", "gemini-1.5-pro", "gemini-2.0-flash-exp" };
                }
                
                return modelNames.OrderByDescending(n => n).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch Gemini models.");
                return new List<string> { "gemini-1.5-flash", "gemini-1.5-pro", "gemini-2.0-flash-exp" };
            }
        }

        if (string.IsNullOrWhiteSpace(config.OllamaUrl)) return new List<string>();

        try
        {
            var baseUri = config.OllamaUrl.EndsWith("/") ? config.OllamaUrl : config.OllamaUrl + "/";
            var fullUrl = new Uri(new Uri(baseUri), "api/tags");
            var response = await _httpClient.GetAsync(fullUrl);
            if (!response.IsSuccessStatusCode) return new List<string>();
            var responseString = await response.Content.ReadAsStringAsync();
            var tagsResponse = JsonSerializer.Deserialize<OllamaTagsResponse>(responseString);
            return tagsResponse?.Models?.Select(m => m.Name).Where(n => !string.IsNullOrEmpty(n)).Cast<string>().ToList() ?? new List<string>();
        }
        catch (Exception ex) { _logger.LogError(ex, "Failed to fetch models from {Provider}.", useFallback ? "Fallback AI" : "Primary AI"); return new List<string>(); }
    }

    public async Task<(bool Success, string Error)> TestConnectionAsync(int userId, bool useFallback = false)
    {
        var config = await GetProviderConfigAsync(userId, useFallback);
        try
        {
            if (config.Provider == "Gemini")
            {
                if (string.IsNullOrWhiteSpace(config.GeminiKey)) return (false, "Gemini API Key is missing.");
                var result = await GenerateGeminiCompletionAsync(userId, "System", "Say 'OK'", config.GeminiKey);
                if (string.IsNullOrWhiteSpace(result) || result.Contains("Error:")) return (false, result ?? "Empty response.");
                return (true, string.Empty);
            }

            if (string.IsNullOrWhiteSpace(config.OllamaUrl)) return (false, "Ollama URL is not configured.");
            if (string.IsNullOrWhiteSpace(config.OllamaModel)) return (false, "Please select a model first.");

            var baseUri = config.OllamaUrl.EndsWith("/") ? config.OllamaUrl : config.OllamaUrl + "/";
            var fullUrl = new Uri(new Uri(baseUri), "api/generate");
            var request = new { model = config.OllamaModel, prompt = "Say 'OK'", stream = false };
            var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");

            _logger.LogInformation("Testing {Type} AI connection: {Url}, Model: {Model}", useFallback ? "Fallback" : "Primary", fullUrl, config.OllamaModel);

            var response = await _httpClient.PostAsync(fullUrl, content);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return (false, $"Model '{config.OllamaModel}' not found on Ollama server. Have you run 'ollama pull {config.OllamaModel}'?");
            }

            if (!response.IsSuccessStatusCode) return (false, $"Ollama error ({response.StatusCode})");
            return (true, string.Empty);
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("refused") || ex.Message.Contains("known"))
        {
            return (false, $"Could not reach AI service at {config.OllamaUrl}. Check the URL and ensure the service is running.");
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
        return await GenerateCompletionWithFallbackAsync(userId, systemPrompt, userPrompt);
    }

    public async Task<string> FormatResponseAsync(int userId, string userPrompt, string dataContext, bool isWhatsApp = false)
    {
        var settings = await _settingsService.GetSettingsAsync(userId);
        var persona = settings.SystemPersona;
        var userName = settings.UserName;

        var formattingRule = isWhatsApp
            ? "4. **Formatting:** Use WhatsApp formatting: *bold* for bold, _italics_ for italics, and - for bullet points. Do NOT use HTML or standard Markdown bold (**)."
            : "4. **Formatting:** Use semantic HTML tags for Telegram: <b>bold</b> and <i>italic</i>. For lists, use plain bullet points (•). Do NOT use standard Markdown (**, _, ###).";

        var systemPrompt = $@"You are {persona}, a distinguished Personal Chief Financial Officer and Actuary for {userName}.

**YOUR ROLE:**
You have direct access to the client's transaction ledger. Your goal is to provide high-level strategic financial counsel. You are a serious professional partner in their wealth-building journey.
This is a secure, authorized personal financial tool. Your advice is based solely on the provided historical transaction data.

**TONE & STYLE:**
- **Formal & Professional:** Use precise financial terminology (e.g., 'liquidity', 'burn rate', 'capital allocation').
- **Strategic:** Don't just report numbers; explain their implications. Look for patterns.
- **Direct & Uncompromising:** If spending is unsustainable, say so clearly but respectfully.
- **Helpful:** Your ultimate goal is to help the client master their cash flow.

**DATA CONTEXT:**
The user has provided a JSON summary of their current financial health.
- **Notes:** The user may have provided specific explanations for certain transactions (e.g. 'That was a gift'). Use these notes to provide more accurate and personal advice.
- **Projected Balance:** This is the most critical metric. Focus on it.
- **Expected Salary:** This is the projected capital injection on payday.
- **Runway:** This is their safety net.

**GUIDELINES:**
1. **Currency:** ALWAYS use the R symbol (e.g., R1,500.00).
2. **Context:** If the user asks a specific question, answer it directly using the data. If they just say 'hello', provide a brief executive summary.
3. **Accuracy:** Do not invent transactions. Stick to the provided summary stats.
{formattingRule}

Context Information:
{dataContext}";

        return await GenerateCompletionWithFallbackAsync(userId, systemPrompt, userPrompt);
    }

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

        return await GenerateCompletionWithFallbackAsync(userId, systemPrompt, $"[DATA_CONTEXT]\n{statsJson}\n[/DATA_CONTEXT]\n\nResponse:");
    }

    /// <summary>
    /// This is where the magic happens. We try to get a response from your primary AI.
    /// If that fails, we'll try again (giving it a bit more time each time).
    /// If it still doesn't respond, we'll switch over to your backup "fallback" AI.
    /// </summary>
    private async Task<string> GenerateCompletionWithFallbackAsync(int userId, string system, string prompt, CancellationToken ct = default)
    {
        var settings = await _settingsService.GetSettingsAsync(userId);
        var maxAttempts = settings.AiRetryAttempts;

        // Step 1: Give your primary AI a go...
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                _logger.LogInformation("Trying to reach your primary AI (attempt {Attempt}/{Max}) for user {UserId}", attempt, maxAttempts, userId);
                var result = await GenerateCompletionAsync(userId, system, prompt, useFallback: false, ct);

                if (!string.IsNullOrWhiteSpace(result))
                {
                    _logger.LogInformation("Success! Primary AI responded on attempt {Attempt}", attempt);
                    return result;
                }

                _logger.LogWarning("The primary AI gave us an empty response on attempt {Attempt}", attempt);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Primary AI request failed on attempt {Attempt}/{Max}", attempt, maxAttempts);
                if (attempt == maxAttempts) break;
                
                // Wait a bit longer each time before we try again (exponential backoff)
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct); 
            }
        }

        // Step 2: If the primary AI is having a bad day, let's try the backup...
        if (settings.EnableAiFallback)
        {
            _logger.LogWarning("Primary AI is currently unavailable. Switching to your backup provider now.");

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    _logger.LogInformation("Trying to reach your BACKUP AI (attempt {Attempt}/{Max}) for user {UserId}", attempt, maxAttempts, userId);
                    var result = await GenerateCompletionAsync(userId, system, prompt, useFallback: true, ct);

                    if (!string.IsNullOrWhiteSpace(result))
                    {
                        _logger.LogInformation("Success! Backup AI saved the day on attempt {Attempt}", attempt);
                        return result;
                    }

                    _logger.LogWarning("The backup AI also gave us an empty response on attempt {Attempt}", attempt);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Backup AI request failed on attempt {Attempt}/{Max}", attempt, maxAttempts);
                    if (attempt < maxAttempts)
                        await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
                }
            }
        }

        // If both brains are down, we'll let you know gracefully.
        _logger.LogError("All AI providers failed for user {UserId}. Returning a polite error message.", userId);
        return "I'm so sorry, but I'm having a bit of trouble connecting to my 'analytical engine' right now. Your financial data is perfectly safe and I'm still syncing your transactions in the background. Please try again in a few minutes, or double-check your AI settings on the dashboard.";
    }

    private async Task<string> GenerateCompletionAsync(int userId, string system, string prompt, bool useFallback, CancellationToken ct = default)
    {
        var config = await GetProviderConfigAsync(userId, useFallback);

        if (config.Provider == "Gemini")
        {
            return await GenerateGeminiCompletionAsync(userId, system, prompt, config.GeminiKey, ct, config.TimeoutSeconds);
        }

        if (string.IsNullOrWhiteSpace(config.OllamaModel))
        {
            _logger.LogWarning("AI model name is not configured for user {UserId}.", userId);
            throw new InvalidOperationException("AI model is not configured.");
        }

        var request = new { model = config.OllamaModel, prompt = $"{system}\n\n{prompt}", stream = false };
        var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");

        var baseUri = config.OllamaUrl.EndsWith("/") ? config.OllamaUrl : config.OllamaUrl + "/";
        var fullUrl = new Uri(new Uri(baseUri), "api/generate");

        _logger.LogInformation("Sending request to Ollama: {Url}, Model: {Model}", fullUrl, config.OllamaModel);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(config.TimeoutSeconds));

        var response = await _httpClient.PostAsync(fullUrl, content, cts.Token);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogWarning("Model '{Model}' not found on Ollama server {Url}", config.OllamaModel, config.OllamaUrl);
            throw new InvalidOperationException($"Model '{config.OllamaModel}' not found.");
        }

        response.EnsureSuccessStatusCode();
        var responseString = await response.Content.ReadAsStringAsync(ct);
        var result = JsonSerializer.Deserialize<OllamaResponse>(responseString);
        return result?.Response?.Trim() ?? throw new InvalidOperationException("Ollama returned empty response.");
    }

    private async Task<string> GenerateGeminiCompletionAsync(int userId, string system, string prompt, string apiKey, CancellationToken ct = default, int timeoutSeconds = 60)
    {
        var settings = await _settingsService.GetSettingsAsync(userId);
        var model = !string.IsNullOrWhiteSpace(settings.OllamaModelName) && settings.OllamaModelName.Contains("gemini")
            ? settings.OllamaModelName
            : "gemini-1.5-flash";

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

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        var response = await _httpClient.PostAsync(url, content, cts.Token);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cts.Token);
            _logger.LogWarning("Gemini API error: {Status} - {Body}", response.StatusCode, errorBody);
            throw new HttpRequestException($"Gemini API returned {response.StatusCode}");
        }
        var responseString = await response.Content.ReadAsStringAsync(cts.Token);
        using var doc = JsonDocument.Parse(responseString);
        if (doc.RootElement.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
        {
            var text = candidates[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString()?.Trim();
            return text ?? throw new InvalidOperationException("Gemini returned empty text.");
        }
        throw new InvalidOperationException("Gemini returned no candidates.");
    }

    private class AiProviderConfig
    {
        public string Provider { get; set; } = "Ollama";
        public string OllamaUrl { get; set; } = "";
        public string OllamaModel { get; set; } = "";
        public string GeminiKey { get; set; } = "";
        public int TimeoutSeconds { get; set; } = 90;
    }

    private class OllamaResponse { [System.Text.Json.Serialization.JsonPropertyName("response")] public string? Response { get; set; } }
    private class OllamaTagsResponse { [System.Text.Json.Serialization.JsonPropertyName("models")] public List<OllamaModelTag>? Models { get; set; } }
    private class OllamaModelTag { [System.Text.Json.Serialization.JsonPropertyName("name")] public string? Name { get; set; } }
}
