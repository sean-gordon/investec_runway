using System.Text;
using System.Text.Json;
using System.Threading;
using System.Collections.Concurrent;
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
    Task<(bool Success, string Error)> TestConnectionAsync(int userId, bool useFallback = false, bool useThinking = false, bool forceRefresh = false);
    Task<List<string>> GetAvailableModelsAsync(int userId, bool useFallback = false, bool useThinking = false, AppSettings? overriddenSettings = null);
    Task<List<Transaction>> CategorizeTransactionsAsync(int userId, List<Transaction> transactions);
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
    private readonly ConcurrentDictionary<string, (bool Success, string Error, DateTime Timestamp)> _testCache = new();

    public AiService(HttpClient httpClient, ILogger<AiService> logger, IConfiguration configuration, ISettingsService settingsService)
    {
        _httpClient = httpClient;
        _logger = logger;
        _settingsService = settingsService;
    }

    public async Task<List<Transaction>> CategorizeTransactionsAsync(int userId, List<Transaction> transactions)
    {
        if (transactions == null || !transactions.Any()) return new List<Transaction>();

        // Batch processing to avoid prompt size limits and 429 errors
        const int batchSize = 50;
        for (int i = 0; i < transactions.Count; i += batchSize)
        {
            var batch = transactions.Skip(i).Take(batchSize).ToList();
            await CategorizeBatchAsync(userId, batch);
        }

        return transactions;
    }

    private async Task CategorizeBatchAsync(int userId, List<Transaction> batch)
    {
        var txData = batch.Select(t => new { t.Id, t.Description, t.Amount }).ToList();
        var txJson = JsonSerializer.Serialize(txData);

        var systemPrompt = @"You are a financial data classifier for the Gordon Finance Engine.
YOUR GOAL: Categorize bank transactions into semantic categories with high precision.

CATEGORIES & RULES:
- Groceries: Supermarkets, butcheries, bakeries (e.g., Checkers, Woolworths, Pick n Pay, Spar).
- Eating Out: Restaurants, fast food, coffee shops, bars (e.g., Uber Eats, McDonald's, Starbucks).
- Transport: Fuel, ride-sharing, tolls, car rentals, public transport (e.g., Engen, Shell, Uber, Bolt, Gautrain).
- Shopping: Retailers, clothing, electronics, home goods, Amazon, Takealot.
- Bills & Utilities: Electricity, water, rates, taxes, insurance, medical aid (e.g., City of Johannesburg, Discovery Health, Outsurance).
- Subscriptions: Recurring digital services (e.g., Netflix, Spotify, Apple, Google, Microsoft, LinkedIn, Gym memberships).
- Health & Wellness: Pharmacies, doctors, dentists, therapists, fitness.
- Entertainment: Cinema, hobbies, events, gaming, betting.
- Travel: Flights, hotels, Airbnb, travel agencies.
- Personal Care: Hairdressers, spas, salons.
- Education: School fees, university, courses, books.
- Finance: Bank fees, interest, loan repayments (NOT internal transfers).
- Transfer: Moving money between the user's own accounts (Internal transfers, credit card payments from current account).
- Income: Salary, dividends, refunds, gifts RECEIVED (Note: amount is POSITIVE for income).
- Expense: Any payment, debit, or purchase (Note: amount is NEGATIVE for expenses).
- General: Anything that doesn't fit the above or is ambiguous.

POLARITY RULE: In this system, NEGATIVE (-) indicates money LEAVING (Expense), and POSITIVE (+) indicates money ENTERING (Income). Use this as a primary lead for categorization.

CONTEXT: The user is likely in South Africa.
INPUT: A JSON list of transactions with 'id', 'description', and 'amount'.
OUTPUT: A JSON list of objects with 'id' and 'category'.

IMPORTANT:
1. Be semantically smart (e.g., 'Woolworths Food' is Groceries, 'Woolworths' alone might be Shopping but usually Groceries).
2. 'Uber' is Transport, 'Uber Eats' is Eating Out.
3. Return ONLY the JSON array. NO other text.";

        try
        {
            var jsonResponse = await GenerateCompletionWithFallbackAsync(userId, systemPrompt, $"TRANSACTIONS:\n{txJson}");
            
            var match = System.Text.RegularExpressions.Regex.Match(jsonResponse, @"```json\s*(.*?)\s*```", System.Text.RegularExpressions.RegexOptions.Singleline);
            var cleanJson = match.Success ? match.Groups[1].Value : jsonResponse.Trim();

            if (cleanJson.StartsWith("I'm") || cleanJson.Contains("error"))
            {
                _logger.LogWarning("AI returned a non-JSON response for categorization: {Response}", cleanJson);
                return;
            }

            using var doc = JsonDocument.Parse(cleanJson);
            var results = new Dictionary<Guid, string>();
            
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var idStr = item.GetProperty("id").GetString();
                if (Guid.TryParse(idStr, out var id))
                {
                    var cat = item.GetProperty("category").GetString() ?? "General";
                    results[id] = cat;
                }
            }

            foreach (var tx in batch)
            {
                if (results.TryGetValue(tx.Id, out var category))
                {
                    tx.Category = category;
                    tx.IsAiProcessed = true;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to batch categorize transactions for user {UserId}", userId);
        }
    }

    public async Task<(bool IsChartRequest, string? ChartType, string? Sql, string? Title)> AnalyzeChartRequestAsync(int userId, string userMessage)
    {
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        var systemPrompt = $@"You are a financial data architect.
Current Date: {today}
Table 'transactions': id (uuid), user_id (int), transaction_date (timestamptz), description (text), amount (numeric), category (text).
NOTE: Amount NEGATIVE = Expense, POSITIVE = Income.

YOUR GOAL: Detect if the user wants a chart/graph of specific data.

EXAMPLES:
- 'Show me a barchart of Uber Eats' -> {{ ""isChart"": true, ""type"": ""bar"", ""sql"": ""SELECT description as Label, SUM(ABS(amount)) as Value FROM transactions WHERE user_id = @userId AND description ILIKE '%UBER EATS%' GROUP BY description"", ""title"": ""Uber Eats Spending"" }}
- 'Graph my total spending per day' -> {{ ""isChart"": true, ""type"": ""line"", ""sql"": ""SELECT transaction_date::date as Label, SUM(ABS(amount)) as Value FROM transactions WHERE user_id = @userId AND amount < 0 GROUP BY 1 ORDER BY 1"", ""title"": ""Daily Spending Trend"" }}
- 'Who are my top 5 categories?' -> {{ ""isChart"": true, ""type"": ""bar"", ""sql"": ""SELECT category as Label, SUM(ABS(amount)) as Value FROM transactions WHERE user_id = @userId AND amount < 0 GROUP BY 1 ORDER BY 2 DESC LIMIT 5"", ""title"": ""Top 5 Categories"" }}

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
                ModelName = settings.FallbackAiProvider == "Gemini" ? settings.FallbackGeminiModelName : settings.FallbackOllamaModelName,
                GeminiKey = settings.FallbackGeminiApiKey,
                TimeoutSeconds = settings.AiTimeoutSeconds,
                RetryAttempts = settings.AiRetryAttempts
            };
        }

        return new AiProviderConfig
        {
            Provider = settings.AiProvider,
            OllamaUrl = settings.OllamaBaseUrl,
            ModelName = settings.AiProvider == "Gemini" ? settings.GeminiModelName : settings.OllamaModelName,
            GeminiKey = settings.GeminiApiKey,
            TimeoutSeconds = settings.AiTimeoutSeconds,
            RetryAttempts = settings.AiRetryAttempts
        };
    }

    private async Task<AiProviderConfig> GetThinkingProviderConfigAsync(int userId, AppSettings? overriddenSettings = null)
    {
        var settings = overriddenSettings ?? await _settingsService.GetSettingsAsync(userId);
        return new AiProviderConfig
        {
            Provider = settings.ThinkingAiProvider,
            OllamaUrl = settings.ThinkingOllamaBaseUrl,
            ModelName = settings.ThinkingAiProvider == "Gemini" ? settings.ThinkingGeminiModelName : settings.ThinkingOllamaModelName,
            GeminiKey = settings.ThinkingGeminiApiKey,
            TimeoutSeconds = settings.AiTimeoutSeconds,
            RetryAttempts = settings.AiRetryAttempts
        };
    }

        public async Task<List<string>> GetAvailableModelsAsync(int userId, bool useFallback = false, bool useThinking = false, AppSettings? overriddenSettings = null)        
        {
            var config = useThinking 
                ? await GetThinkingProviderConfigAsync(userId, overriddenSettings)
                : await GetProviderConfigAsync(userId, useFallback, overriddenSettings);
    
            if (config.Provider == "Gemini")        {
            if (string.IsNullOrWhiteSpace(config.GeminiKey)) return new List<string>();
            try
            {
                var url = $"https://generativelanguage.googleapis.com/v1beta/models?key={config.GeminiKey}";
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var response = await _httpClient.GetAsync(url, cts.Token);
                if (!response.IsSuccessStatusCode) return useThinking ? new List<string> { "gemini-2.0-flash-thinking-exp" } : new List<string> { "gemini-3-flash-preview", "gemini-2.5-flash", "gemini-2.0-flash", "gemini-1.5-flash", "gemini-1.5-pro" };

                var responseString = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(responseString);
                var modelNames = new List<string>();

                if (doc.RootElement.TryGetProperty("models", out var models))
                {
                    foreach (var m in models.EnumerateArray())
                    {
                        var name = m.GetProperty("name").GetString() ?? "";
                        var methods = m.TryGetProperty("supportedGenerationMethods", out var methodsEl) 
                            ? methodsEl.EnumerateArray().Select(x => x.GetString()).ToList() 
                            : new List<string?>();

                        // Dynamic check: If it can generate content and isn't an embedding/vision-only tool, we want it.
                        if (methods.Contains("generateContent"))
                        {
                            if (name.Contains("/")) name = name.Split('/').Last();
                            
                            // Filter out internal/specialized models we know won't work for chat
                            var lowerName = name.ToLower();
                            if (!lowerName.Contains("embedding") && !lowerName.Contains("aqa") && !lowerName.Contains("classifier"))
                            {
                                modelNames.Add(name);
                            }
                        }
                    }
                }
                
                if (!modelNames.Any())
                {
                    _logger.LogWarning("No suitable Gemini models found in API response. Returning defaults.");
                    return new List<string> { "Error: No Gemini models found in payload" };
                }
                
                return modelNames.Distinct().OrderByDescending(n => n).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch Gemini models from Google API.");
                return new List<string> { $"Gemini Fetch Error: {ex.Message}" };
            }
        }

        if (string.IsNullOrWhiteSpace(config.OllamaUrl)) return new List<string> { "Error: Missing Ollama URL" };

        try
        {
            var baseUrl = config.OllamaUrl;
            if (baseUrl.Contains("/api/generate")) baseUrl = baseUrl.Replace("/api/generate", "");
            else if (baseUrl.EndsWith("/api")) baseUrl = baseUrl[..^4];
            
            var baseUri = baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/";
            var fullUrl = new Uri(new Uri(baseUri), "api/tags");
            
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var response = await _httpClient.GetAsync(fullUrl, cts.Token);
            if (!response.IsSuccessStatusCode) return new List<string> { $"Ollama Error HTTP {response.StatusCode}" };
            var responseString = await response.Content.ReadAsStringAsync();
            var tagsResponse = JsonSerializer.Deserialize<OllamaTagsResponse>(responseString);
            return tagsResponse?.Models?.Select(m => m.Name).Where(n => !string.IsNullOrEmpty(n)).Cast<string>().ToList() ?? new List<string> { "Error: Empty models array from Ollama" };
        }
        catch (Exception ex) { _logger.LogError(ex, "Failed to fetch models from {Provider}.", useFallback ? "Fallback AI" : "Primary AI"); return new List<string> { $"Ollama Fetch Error: {ex.Message}" }; }
    }

    public async Task<(bool Success, string Error)> TestConnectionAsync(int userId, bool useFallback = false, bool useThinking = false, bool forceRefresh = false)
    {
        var cacheKey = $"{userId}_{useFallback}_{useThinking}";
        if (!forceRefresh && _testCache.TryGetValue(cacheKey, out var cached) && (DateTime.UtcNow - cached.Timestamp).TotalMinutes < 15)
        {
            return (cached.Success, cached.Error);
        }

        var config = useThinking 
            ? await GetThinkingProviderConfigAsync(userId)
            : await GetProviderConfigAsync(userId, useFallback);

        var testTimeout = TimeSpan.FromSeconds(config.TimeoutSeconds > 0 ? config.TimeoutSeconds : 15); 
        var maxAttempts = config.RetryAttempts > 0 ? config.RetryAttempts : 1; 

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                if (config.Provider == "Gemini")
                {
                    if (string.IsNullOrWhiteSpace(config.GeminiKey)) return (false, "Gemini API Key is missing.");
                    
                    try 
                    {
                        var result = await GenerateGeminiCompletionAsync(userId, "System", "Say 'OK'", config.GeminiKey, config.ModelName, timeoutSeconds: (int)testTimeout.TotalSeconds);
                        var finalResult = (Success: !string.IsNullOrWhiteSpace(result) && !result.Contains("Error:"), Error: result ?? "Empty response.");
                        _testCache[cacheKey] = (finalResult.Success, finalResult.Error, DateTime.UtcNow);
                        return finalResult;
                    }
                    catch (HttpRequestException ex) when (ex.Message.Contains("TooManyRequests") || ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                    {
                        _logger.LogWarning("Gemini Rate Limit hit during test.");
                        if (attempt < maxAttempts) continue;
                        return (false, "Rate Limited - Gemini is cooling down. Please wait a few minutes.");
                    }
                }

                if (string.IsNullOrWhiteSpace(config.OllamaUrl)) return (false, "Ollama URL is not configured.");
                if (string.IsNullOrWhiteSpace(config.ModelName)) return (false, "Please select a model first.");

                var baseUri = config.OllamaUrl.EndsWith("/") ? config.OllamaUrl : config.OllamaUrl + "/";
                var fullUrl = new Uri(new Uri(baseUri), "api/generate");
                var request = new { 
                    model = config.ModelName, 
                    prompt = "Say 'OK'", 
                    stream = false,
                    keep_alive = -1 
                };
                var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");

                _logger.LogInformation("Testing {Type} AI connection (Attempt {Attempt}): {Url}, Model: {Model}", 
                    useFallback ? "Fallback" : "Primary", attempt, fullUrl, config.ModelName);

                using var cts = new CancellationTokenSource(testTimeout);
                var response = await _httpClient.PostAsync(fullUrl, content, cts.Token);

                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    return (false, $"Model '{config.ModelName}' not found on Ollama server.");
                }

                if (!response.IsSuccessStatusCode)
                {
                    if (attempt < maxAttempts) continue;
                    return (false, $"Ollama error ({response.StatusCode})");
                }

                _testCache[cacheKey] = (true, string.Empty, DateTime.UtcNow);
                return (true, string.Empty);
            }
            catch (OperationCanceledException)
            {
                if (attempt < maxAttempts) continue;
                return (false, "Connection timed out after 15s.");
            }
            catch (HttpRequestException ex) when (ex.Message.Contains("refused") || ex.Message.Contains("known"))
            {
                if (attempt < maxAttempts) continue;
                return (false, $"Could not reach AI service at {config.OllamaUrl}.");
            }
            catch (Exception ex) 
            { 
                if (attempt < maxAttempts) continue;
                _logger.LogError(ex, "AI Connection test failed."); 
                return (false, ex.Message); 
            }
        }

        return (false, "Unknown failure during AI connection test.");
    }

    public async Task<string> GenerateSqlAsync(int userId, string userPrompt)
    {
        var today = DateTime.Today.ToString("yyyy-MM-dd");

        // SECURITY FIX: Sanitize user prompt to prevent prompt injection
        var sanitizedPrompt = userPrompt
            .Replace(";", "")      // Prevent multi-statement
            .Replace("--", "")     // Prevent comments
            .Replace("/*", "")     // Prevent block comments
            .Replace("*/", "");

        var systemPrompt = $@"You are a PostgreSQL expert for a financial database.
Current Date: {today}

Table 'transactions' schema:
- transaction_date (timestamptz)
- description (text)
- amount (numeric): IMPORTANT - NEGATIVE numbers are Expenses (Debits), POSITIVE numbers are Income/Credits (Deposits).
- balance (numeric)
- category (text)

**CRITICAL SECURITY RULES:**
1. Return ONLY a single SELECT statement.
2. DO NOT return any DML or DDL (INSERT, UPDATE, DELETE, DROP, ALTER, TRUNCATE).
3. If the user request implies a destructive action, return 'SELECT ''Unauthorized'' as Error'.
4. Use ILIKE for case-insensitive text matching.
5. Filter by user_id = @userId ALWAYS.

Return ONLY the raw SQL query. Do NOT use Markdown formatting (no ```sql). Do NOT include explanations.";
        
        var response = await GenerateCompletionWithFallbackAsync(userId, systemPrompt, sanitizedPrompt);
        
        // Safety check: If the AI failed and returned the graceful error message, 
        // we MUST NOT return it as SQL, otherwise it will crash the DB caller.
        if (response.StartsWith("I'm so sorry") || response.Contains("analytical engine") || response.Trim().StartsWith("SELECT 'Unauthorized'"))
        {
            throw new InvalidOperationException("AI failed to generate a valid or authorized SQL query.");
        }

        return response;
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
- **Seasonality (YoY):** You have access to 'SpendSameMonthLastYear' and 'YoYChangePercentage'. Use these to identify annual cycles (e.g. 'Your spending is up 10% vs last February, which is expected due to school fees').

**GUIDELINES:**
1. **Currency:** ALWAYS use the R symbol (e.g., R1,500.00).
2. **Context:** If the user asks a specific question, answer it directly using the data. If they just say 'hello', provide a brief executive summary.
3. **Accuracy:** Do not invent transactions. Stick to the provided summary stats.
4. **Seasonality:** If the user asks about trends, look at the YoY metrics to see if current spending is normal for this time of year.
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
    3. **Seasonality Analysis:** Examine 'YoYChangePercentage'. Compare current spending to 'SpendSameMonthLastYear' to determine if spikes are part of an annual cycle or anomalous behaviour.
    4. **Variance Analysis:** Scrutinize 'TopCategoriesWithIncreases'. If variable spending is trending upward, identify the root cause (the specific category) and recommend a course correction.
    5. **Risk Profile:** Comment on the 'RunwayDays' and 'ProbabilityToReachPayday'. Frame this in terms of financial security.

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
        var finalPrompt = prompt;

        // Step 0: Let the "Thinking Model" have a crack at it first, if enabled and different from primary
        if (settings.EnableThinkingModel)
        {
            var primaryConfig = await GetProviderConfigAsync(userId, useFallback: false);
            var thinkingConfig = await GetThinkingProviderConfigAsync(userId);

            // Only use thinking if it's actually a different configuration
            bool isDifferent = thinkingConfig.Provider != primaryConfig.Provider || 
                               thinkingConfig.ModelName != primaryConfig.ModelName || 
                               thinkingConfig.OllamaUrl != primaryConfig.OllamaUrl;

            if (isDifferent)
            {
                try
                {
                    _logger.LogInformation("Engaging Thinking Model ({Provider}:{Model}) for user {UserId}", thinkingConfig.Provider, thinkingConfig.ModelName, userId);
                    var thinkingSystem = "Analyze this query and provide deep reasoning and a breakdown of the steps needed to answer it accurately. Be strategic. If this is a report request, outline the key financial insights that should be highlighted.";
                    var reasoning = await GenerateCompletionAsync(userId, thinkingSystem, prompt, useFallback: false, ct, useThinking: true);
                    
                    if (!string.IsNullOrWhiteSpace(reasoning))
                    {
                        finalPrompt = $"[REASONING_AND_STRATEGY]\n{reasoning}\n[/REASONING_AND_STRATEGY]\n\n[ORIGINAL_QUERY]\n{prompt}\n[/ORIGINAL_QUERY]";
                        _logger.LogInformation("Thinking Model step complete for user {UserId}", userId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Thinking model failed for user {UserId}. Proceeding without additional reasoning.", userId);
                }
            }
        }

        var maxAttempts = settings.AiRetryAttempts;

        // Step 1: Give your primary AI a go...
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                _logger.LogInformation("Trying to reach your primary AI (attempt {Attempt}/{Max}) for user {UserId}", attempt, maxAttempts, userId);
                var result = await GenerateCompletionAsync(userId, system, finalPrompt, useFallback: false, ct);

                if (!string.IsNullOrWhiteSpace(result))
                {
                    _logger.LogInformation("Success! Primary AI responded on attempt {Attempt}", attempt);
                    var review = await ReviewOutputWithThinkingModelAsync(userId, system, prompt, result, settings, ct);
                    if (review.IsApproved) return result;
                    
                    _logger.LogWarning("Thinking model rejected primary AI output. Sending feedback for rewrite.");
                    finalPrompt = $"[PREVIOUS_RESPONSE_REJECTED]\n{result}\n[/PREVIOUS_RESPONSE_REJECTED]\n\n[REVIEW_FEEDBACK]\n{review.Feedback}\n[/REVIEW_FEEDBACK]\n\n[ORIGINAL_QUERY]\n{prompt}\n[/ORIGINAL_QUERY]\n\nPlease try again and fix the issues mentioned in the feedback.";
                    continue; // Loop again!
                }

                _logger.LogWarning("The primary AI gave us an empty response on attempt {Attempt}", attempt);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Primary AI request failed on attempt {Attempt}/{Max}", attempt, maxAttempts);
                if (attempt == maxAttempts) break;
                
                var delaySeconds = 2 * attempt;
                if (ex is HttpRequestException httpEx && httpEx.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    delaySeconds = 38; // Give Gemini time to cool down based on 'Retry-After: 36s' bounds
                }
                
                // Wait a bit longer each time before we try again (exponential backoff)
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct); 
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
                    var result = await GenerateCompletionAsync(userId, system, finalPrompt, useFallback: true, ct);

                    if (!string.IsNullOrWhiteSpace(result))
                    {
                        _logger.LogInformation("Success! Backup AI saved the day on attempt {Attempt}", attempt);
                        var review = await ReviewOutputWithThinkingModelAsync(userId, system, prompt, result, settings, ct);
                        if (review.IsApproved) return result;

                        _logger.LogWarning("Thinking model rejected backup AI output. Sending feedback for rewrite.");
                        finalPrompt = $"[PREVIOUS_RESPONSE_REJECTED]\n{result}\n[/PREVIOUS_RESPONSE_REJECTED]\n\n[REVIEW_FEEDBACK]\n{review.Feedback}\n[/REVIEW_FEEDBACK]\n\n[ORIGINAL_QUERY]\n{prompt}\n[/ORIGINAL_QUERY]\n\nPlease try again and fix the issues mentioned in the feedback.";
                        continue;
                    }

                    _logger.LogWarning("The backup AI also gave us an empty response on attempt {Attempt}", attempt);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Backup AI request failed on attempt {Attempt}/{Max}", attempt, maxAttempts);
                    if (attempt == maxAttempts) break;
                    
                    var delaySeconds = 2 * attempt;
                    if (ex is HttpRequestException httpEx && httpEx.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                    {
                        delaySeconds = 38;
                    }
                    
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct);
                }
            }
        }

        // If both brains are down, we'll let you know gracefully.
        _logger.LogError("All AI providers failed for user {UserId}. Returning a polite error message.", userId);
        return "I'm so sorry, but I'm having a bit of trouble connecting to my 'analytical engine' right now. Your financial data is perfectly safe and I'm still syncing your transactions in the background. Please try again in a few minutes, or double-check your AI settings on the dashboard.";
    }

    private async Task<(bool IsApproved, string Feedback)> ReviewOutputWithThinkingModelAsync(int userId, string system, string originalPrompt, string aiResult, AppSettings settings, CancellationToken ct)
    {
        if (!settings.EnableThinkingModel) return (true, "");

        var primaryConfig = await GetProviderConfigAsync(userId, useFallback: false);
        var thinkingConfig = await GetThinkingProviderConfigAsync(userId);
        bool isDifferent = thinkingConfig.Provider != primaryConfig.Provider || 
                           thinkingConfig.ModelName != primaryConfig.ModelName || 
                           thinkingConfig.OllamaUrl != primaryConfig.OllamaUrl;

        if (!isDifferent) return (true, "");

        try
        {
            _logger.LogInformation("Reviewing output with Thinking Model for user {UserId}", userId);
            
            var reviewSystemPrompt = @"You are a strict quality control reviewer. Your job is to review the output of another AI to ensure it directly answers the user's prompt truthfully and accurately, following all rules.
IF the response is perfect: Output EXACTLY '<APPROVED>' and nothing else.
IF the response completely missed the prompt or is missing critical information: Provide specific feedback on what is wrong and what needs to be fixed. Do NOT rewrite the response yourself, just provide the feedback.";
            
            var reviewPrompt = $"[ORIGINAL_SYSTEM_PROMPT]\n{system}\n[/ORIGINAL_SYSTEM_PROMPT]\n\n[USER_PROMPT]\n{originalPrompt}\n[/USER_PROMPT]\n\n[AI_PROPOSED_RESPONSE]\n{aiResult}\n[/AI_PROPOSED_RESPONSE]\n\nAnalyze the AI_PROPOSED_RESPONSE. If it is high quality and addresses the USER_PROMPT according to the ORIGINAL_SYSTEM_PROMPT, output strictly <APPROVED>. If it is bad, write down exactly what is wrong so the AI can try again.";

            var reviewedResult = await GenerateCompletionAsync(userId, reviewSystemPrompt, reviewPrompt, useFallback: false, ct, useThinking: true);

            if (!string.IsNullOrWhiteSpace(reviewedResult))
            {
                _logger.LogInformation("Thinking Model review complete for user {UserId}", userId);
                if (reviewedResult.Trim().Contains("<APPROVED>")) return (true, "");
                return (false, reviewedResult);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Thinking model failed to review output for user {UserId}. Returning original output.", userId);
        }

        return (true, "");
    }

    private async Task<string> GenerateCompletionAsync(int userId, string system, string prompt, bool useFallback, CancellationToken ct = default, bool useThinking = false)
    {
        var config = useThinking 
            ? await GetThinkingProviderConfigAsync(userId)
            : await GetProviderConfigAsync(userId, useFallback);

        if (config.Provider == "Gemini")
        {
            return await GenerateGeminiCompletionAsync(userId, system, prompt, config.GeminiKey, config.ModelName, ct, config.TimeoutSeconds);
        }

        if (string.IsNullOrWhiteSpace(config.ModelName))
        {
            _logger.LogWarning("AI model name is not configured for user {UserId}.", userId);
            throw new InvalidOperationException("AI model is not configured.");
        }

        var request = new { 
            model = config.ModelName, 
            prompt = $"{system}\n\n{prompt}", 
            stream = false,
            keep_alive = -1 // Keep model in memory indefinitely
        };
        var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");

        var baseUri = config.OllamaUrl.EndsWith("/") ? config.OllamaUrl : config.OllamaUrl + "/";
        var fullUrl = new Uri(new Uri(baseUri), "api/generate");

        _logger.LogInformation("Sending request to Ollama: {Url}, Model: {Model}", fullUrl, config.ModelName);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        
        // Use a more generous timeout for Ollama (especially on first load)
        var timeout = Math.Max(config.TimeoutSeconds, 180); 
        cts.CancelAfter(TimeSpan.FromSeconds(timeout));

        var response = await _httpClient.PostAsync(fullUrl, content, cts.Token);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogWarning("Model '{Model}' not found on Ollama server {Url}", config.ModelName, config.OllamaUrl);
            throw new InvalidOperationException($"Model '{config.ModelName}' not found.");
        }

        response.EnsureSuccessStatusCode();
        var responseString = await response.Content.ReadAsStringAsync(ct);
        var result = JsonSerializer.Deserialize<OllamaResponse>(responseString);
        return result?.Response?.Trim() ?? throw new InvalidOperationException("Ollama returned empty response.");
    }

    private async Task<string> GenerateGeminiCompletionAsync(int userId, string system, string prompt, string apiKey, string modelName, CancellationToken ct = default, int timeoutSeconds = 60)
    {
        var model = !string.IsNullOrWhiteSpace(modelName) && modelName.Contains("gemini")
            ? modelName
            : "gemini-3-flash-preview";

        // The base URL path expected by Google is v1beta/models/{model}:generateContent
        // We ensure we don't have double 'models/' in the path.
        if (model.StartsWith("models/")) model = model.Replace("models/", "");
        
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
            throw new HttpRequestException($"Gemini API returned {response.StatusCode}", null, response.StatusCode);
        }
        var responseString = await response.Content.ReadAsStringAsync(cts.Token);
        using var doc = JsonDocument.Parse(responseString);
        
        if (doc.RootElement.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
        {
            var firstCandidate = candidates[0];
            
            if (firstCandidate.TryGetProperty("content", out var candidateContent) && 
                candidateContent.TryGetProperty("parts", out var parts) && 
                parts.GetArrayLength() > 0)
            {
                var text = parts[0].GetProperty("text").GetString()?.Trim();
                return text ?? throw new InvalidOperationException("Gemini returned empty text.");
            }

            if (firstCandidate.TryGetProperty("finishReason", out var reason))
            {
                var reasonStr = reason.GetString();
                _logger.LogWarning("Gemini failed to generate content. Reason: {Reason}", reasonStr);
                return $"I'm sorry, but I couldn't generate a response (Reason: {reasonStr}). This usually happens if the AI's safety filters are triggered by the financial data or the query.";
            }
        }

        if (doc.RootElement.TryGetProperty("error", out var error))
        {
            var errorMsg = error.TryGetProperty("message", out var msg) ? msg.GetString() : "Unknown API error";
            _logger.LogError("Gemini API Error: {Message}", errorMsg);
            throw new InvalidOperationException($"Gemini API error: {errorMsg}");
        }

        throw new InvalidOperationException("Gemini returned no valid candidates or content.");
    }

    private class AiProviderConfig
    {
        public string Provider { get; set; } = "Ollama";
        public string OllamaUrl { get; set; } = "";
        public string ModelName { get; set; } = "";
        public string GeminiKey { get; set; } = "";
        public int TimeoutSeconds { get; set; } = 90;
        public int RetryAttempts { get; set; } = 2;
    }

    private class OllamaResponse { [System.Text.Json.Serialization.JsonPropertyName("response")] public string? Response { get; set; } }
    private class OllamaTagsResponse { [System.Text.Json.Serialization.JsonPropertyName("models")] public List<OllamaModelTag>? Models { get; set; } }
    private class OllamaModelTag { [System.Text.Json.Serialization.JsonPropertyName("name")] public string? Name { get; set; } }
}
