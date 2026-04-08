using System.Text;
using System.Text.Json;
using System.Threading;
using GordonWorker.Models;

namespace GordonWorker.Services;

public interface IAiService
{
    Task<string> GenerateSqlAsync(int userId, string userPrompt);
    Task<string> FormatResponseAsync(int userId, string userPrompt, string dataContext, bool isWhatsApp = false, CancellationToken ct = default);
    Task<string> GenerateSimpleReportAsync(int userId, string statsJson);
    Task<(Guid? TransactionId, string? Note)> AnalyzeExpenseExplanationAsync(int userId, string userMessage, List<Transaction> recentTransactions, CancellationToken ct = default);
    Task<(bool IsAffordabilityCheck, decimal? Amount, string? Description)> AnalyzeAffordabilityAsync(int userId, string userMessage);
    Task<(bool IsChartRequest, string? ChartType, string? Sql, string? Title)> AnalyzeChartRequestAsync(int userId, string userMessage);
    Task<(bool Success, string Error)> TestConnectionAsync(int userId, bool useFallback = false, bool useThinking = false, bool forceRefresh = false, AppSettings? overriddenSettings = null);
    Task<List<string>> GetAvailableModelsAsync(int userId, bool useFallback = false, bool useThinking = false, AppSettings? overriddenSettings = null);
    Task<string> GenerateCompletionAsync(int userId, string system, string prompt, bool useFallback = false, CancellationToken ct = default, bool useThinking = false, bool requiresReasoning = false);
    Task<JsonElement?> DetectIntentAsync(int userId, string userMessage, CancellationToken ct = default);
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
    private readonly ClaudeCliService _claudeCliService;

    public AiService(HttpClient httpClient, ILogger<AiService> logger, ISettingsService settingsService, ClaudeCliService claudeCliService)
    {
        _httpClient = httpClient;
        _logger = logger;
        _settingsService = settingsService;
        _claudeCliService = claudeCliService;
    }

    public async Task<JsonElement?> DetectIntentAsync(int userId, string userMessage, CancellationToken ct = default)
    {
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        var systemPrompt = GordonWorker.Prompts.SystemPrompts.GetIntentDetectionPrompt(today);

        var jsonResponse = await GenerateCompletionAsync(userId, systemPrompt, $"USER MESSAGE: \"{userMessage}\"", ct: ct);

        try
        {
            var match = System.Text.RegularExpressions.Regex.Match(jsonResponse, @"```json\s*(.*?)\s*```", System.Text.RegularExpressions.RegexOptions.Singleline);
            var cleanJson = match.Success ? match.Groups[1].Value : jsonResponse.Trim();

            using var doc = JsonDocument.Parse(cleanJson);
            return doc.RootElement.Clone();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Intent detection parse failure. Raw: {Raw}. Error: {Msg}", jsonResponse, ex.Message);
            return null;
        }
    }

    public async Task<(bool IsChartRequest, string? ChartType, string? Sql, string? Title)> AnalyzeChartRequestAsync(int userId, string userMessage)
    {
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        var systemPrompt = GordonWorker.Prompts.SystemPrompts.GetChartAnalysisPrompt(today);

        var jsonResponse = await GenerateCompletionAsync(userId, systemPrompt, $"USER REQUEST: \"{userMessage}\"");

        try
        {
            var match = System.Text.RegularExpressions.Regex.Match(jsonResponse, @"```json\s*(.*?)\s*```", System.Text.RegularExpressions.RegexOptions.Singleline);
            var cleanJson = match.Success ? match.Groups[1].Value : jsonResponse.Trim();

            using var doc = JsonDocument.Parse(cleanJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("isChart", out var isChartEl) && isChartEl.ValueKind == JsonValueKind.True)
            {
                return (
                    true,
                    root.TryGetProperty("type", out var t) ? t.GetString() : "bar",
                    root.TryGetProperty("sql", out var s) ? s.GetString() : null,
                    root.TryGetProperty("title", out var title) ? title.GetString() : "Spending Chart"
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
        var systemPrompt = GordonWorker.Prompts.SystemPrompts.GetAffordabilityPrompt();

        var jsonResponse = await GenerateCompletionAsync(userId, systemPrompt, $"USER MESSAGE: \"{userMessage}\"");

        try
        {
            var match = System.Text.RegularExpressions.Regex.Match(jsonResponse, @"```json\s*(.*?)\s*```", System.Text.RegularExpressions.RegexOptions.Singleline);
            var cleanJson = match.Success ? match.Groups[1].Value : jsonResponse.Trim();

            using var doc = JsonDocument.Parse(cleanJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("isCheck", out var isCheckEl) && isCheckEl.ValueKind == JsonValueKind.True)
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

    public async Task<(Guid? TransactionId, string? Note)> AnalyzeExpenseExplanationAsync(int userId, string userMessage, List<Transaction> recentTransactions, CancellationToken ct = default)
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
        var systemPrompt = GordonWorker.Prompts.SystemPrompts.GetExpenseExplanationPrompt();

        var prompt = $"USER MESSAGE: \"{userMessage}\"\n\nTRANSACTIONS:\n{candidatesJson}";

        var jsonResponse = await GenerateCompletionAsync(userId, systemPrompt, prompt, ct: ct);

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
                    var note = doc.RootElement.TryGetProperty("note", out var noteEl) ? noteEl.GetString() : "User explanation";
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

    private string MapModelName(string provider, string modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName)) return modelName;

        // If it's the Claude CLI provider and contains a date, it might be deprecated.
        // We force it to use stable aliases which the CLI handles well.
        if (provider == "Claude CLI")
        {
            if (modelName.Contains("sonnet")) return "sonnet";
            if (modelName.Contains("haiku")) return "haiku";
            if (modelName.Contains("opus")) return "opus";
        }

        return modelName;
    }

    private async Task<AiProviderConfig> GetProviderConfigAsync(int userId, bool useFallback = false, AppSettings? overriddenSettings = null)
    {
        var settings = overriddenSettings ?? await _settingsService.GetSettingsAsync(userId);

        if (useFallback && settings.EnableAiFallback)
        {
            var provider = settings.FallbackAiProvider;
            _logger.LogInformation("Using fallback AI provider: {Provider}", provider);
            return new AiProviderConfig
            {
                Provider = provider,
                OllamaUrl = settings.FallbackOllamaBaseUrl,
                ModelName = MapModelName(provider, provider switch
                {
                    "Gemini" => settings.FallbackGeminiModelName,
                    "OpenAI" => settings.FallbackOpenAiModelName,
                    "Anthropic" => settings.FallbackAnthropicModelName,
                    "Claude CLI" => settings.FallbackClaudeCliModelName,
                    _ => settings.FallbackOllamaModelName
                }),
                GeminiKey = settings.FallbackGeminiApiKey,
                OpenAiKey = settings.FallbackOpenAiApiKey,
                AnthropicKey = settings.FallbackAnthropicApiKey,
                ClaudeCliToken = settings.FallbackClaudeCliOAuthToken,
                TimeoutSeconds = settings.AiTimeoutSeconds,
                RetryAttempts = settings.AiRetryAttempts
            };
        }

        var primaryProvider = settings.AiProvider;
        return new AiProviderConfig
        {
            Provider = primaryProvider,
            OllamaUrl = settings.OllamaBaseUrl,
            ModelName = MapModelName(primaryProvider, primaryProvider switch
            {
                "Gemini" => settings.GeminiModelName,
                "OpenAI" => settings.OpenAiModelName,
                "Anthropic" => settings.AnthropicModelName,
                "Claude CLI" => settings.ClaudeCliModelName,
                _ => settings.OllamaModelName
            }),
            GeminiKey = settings.GeminiApiKey,
            OpenAiKey = settings.OpenAiApiKey,
            AnthropicKey = settings.AnthropicApiKey,
            ClaudeCliToken = settings.ClaudeCliOAuthToken,
            TimeoutSeconds = settings.AiTimeoutSeconds,
            RetryAttempts = settings.AiRetryAttempts
        };
    }

    private async Task<AiProviderConfig> GetThinkingProviderConfigAsync(int userId, AppSettings? overriddenSettings = null)
    {
        var settings = overriddenSettings ?? await _settingsService.GetSettingsAsync(userId);
        var provider = settings.ThinkingAiProvider;
        return new AiProviderConfig
        {
            Provider = provider,
            OllamaUrl = settings.ThinkingOllamaBaseUrl,
            ModelName = MapModelName(provider, provider switch
            {
                "Gemini" => settings.ThinkingGeminiModelName,
                "OpenAI" => settings.ThinkingOpenAiModelName,
                "Anthropic" => settings.ThinkingAnthropicModelName,
                "Claude CLI" => settings.ThinkingClaudeCliModelName,
                _ => settings.ThinkingOllamaModelName
            }),
            GeminiKey = settings.ThinkingGeminiApiKey,
            OpenAiKey = settings.ThinkingOpenAiApiKey,
            AnthropicKey = settings.ThinkingAnthropicApiKey,
            ClaudeCliToken = settings.ThinkingClaudeCliOAuthToken,
            TimeoutSeconds = settings.AiTimeoutSeconds,
            RetryAttempts = settings.AiRetryAttempts
        };
    }

    public async Task<List<string>> GetAvailableModelsAsync(int userId, bool useFallback = false, bool useThinking = false, AppSettings? overriddenSettings = null)
    {
        var config = useThinking
            ? await GetThinkingProviderConfigAsync(userId, overriddenSettings)
            : await GetProviderConfigAsync(userId, useFallback, overriddenSettings);

        if (config.Provider == "Anthropic" || config.Provider == "Claude CLI")
        {
            return await _claudeCliService.GetAvailableModelsAsync(config.ClaudeCliToken);
        }

        if (config.Provider == "Gemini")
        {
            if (string.IsNullOrWhiteSpace(config.GeminiKey)) return new List<string>();
            try
            {
                var url = "https://generativelanguage.googleapis.com/v1beta/models";
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var modelsReq = new HttpRequestMessage(HttpMethod.Get, url);
                modelsReq.Headers.Add("x-goog-api-key", config.GeminiKey);
                var response = await _httpClient.SendAsync(modelsReq, cts.Token);
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

                        if (methods.Contains("generateContent"))
                        {
                            if (name.Contains("/")) name = name.Split('/').Last();
                            var lowerName = name.ToLower();
                            if (!lowerName.Contains("embedding") && !lowerName.Contains("aqa") && !lowerName.Contains("classifier"))
                            {
                                modelNames.Add(name);
                            }
                        }
                    }
                }
                return modelNames.Distinct().OrderByDescending(n => n).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch Gemini models.");
                return new List<string> { $"Error: {ex.Message}" };
            }
        }

        if (config.Provider == "OpenAI")
        {
            if (string.IsNullOrWhiteSpace(config.OpenAiKey)) return new List<string> { "Error: Missing OpenAI API Key" };
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/models");
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.OpenAiKey);
                var response = await _httpClient.SendAsync(req, cts.Token);
                if (!response.IsSuccessStatusCode) return new List<string> { "gpt-4o", "gpt-4o-mini" };

                var responseString = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(responseString);
                var modelNames = new List<string>();

                if (doc.RootElement.TryGetProperty("data", out var data))
                {
                    foreach (var m in data.EnumerateArray())
                    {
                        var id = m.GetProperty("id").GetString() ?? "";
                        if (id.StartsWith("gpt-") || id.StartsWith("o1") || id.StartsWith("o3"))
                            modelNames.Add(id);
                    }
                }
                return modelNames.OrderByDescending(n => n).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch OpenAI models.");
                return new List<string> { $"Error: {ex.Message}" };
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
            return tagsResponse?.Models?.Select(m => m.Name).Where(n => !string.IsNullOrEmpty(n)).Cast<string>().ToList() ?? new List<string>();
        }
        catch (Exception ex) { _logger.LogError(ex, "Failed to fetch Ollama models."); return new List<string> { $"Error: {ex.Message}" }; }
    }

    public async Task<(bool Success, string Error)> TestConnectionAsync(int userId, bool useFallback = false, bool useThinking = false, bool forceRefresh = false, AppSettings? overriddenSettings = null)
    {
        _ = forceRefresh;
        var config = useThinking
            ? await GetThinkingProviderConfigAsync(userId, overriddenSettings)
            : await GetProviderConfigAsync(userId, useFallback, overriddenSettings);

        const int TestTimeoutCapSeconds = 15;
        var testTimeoutSeconds = Math.Min(config.TimeoutSeconds > 0 ? config.TimeoutSeconds : TestTimeoutCapSeconds, TestTimeoutCapSeconds);
        var testTimeout = TimeSpan.FromSeconds(testTimeoutSeconds);

        try
        {
            using var testCts = new CancellationTokenSource(testTimeout);
            if (config.Provider == "Claude CLI")
            {
                var result = await _claudeCliService.AskClaudeAsync("Say 'OK'", config.ModelName, config.ClaudeCliToken, testCts.Token);
                return (Success: !string.IsNullOrWhiteSpace(result) && !result.Contains("Error:"), Error: result ?? "Empty response.");
            }

            if (config.Provider == "Gemini")
            {
                var result = await GenerateGeminiCompletionAsync(userId, "System", "Say 'OK'", config.GeminiKey, config.ModelName, timeoutSeconds: (int)testTimeout.TotalSeconds);
                return (Success: !string.IsNullOrWhiteSpace(result) && !result.Contains("Error:"), Error: result ?? "Empty response.");
            }

            if (config.Provider == "OpenAI")
            {
                var result = await GenerateOpenAiCompletionAsync(userId, "System", "Say 'OK'", config.OpenAiKey, config.ModelName, timeoutSeconds: (int)testTimeout.TotalSeconds);
                return (Success: !string.IsNullOrWhiteSpace(result) && !result.Contains("Error:"), Error: result ?? "Empty response.");
            }

            if (config.Provider == "Anthropic")
            {
                var result = await GenerateAnthropicCompletionAsync(userId, "System", "Say 'OK'", config.AnthropicKey, config.ModelName, timeoutSeconds: (int)testTimeout.TotalSeconds);
                return (Success: !string.IsNullOrWhiteSpace(result) && !result.Contains("Error:"), Error: result ?? "Empty response.");
            }

            if (string.IsNullOrWhiteSpace(config.OllamaUrl)) return (false, "Ollama URL is not configured.");
            
            var baseUrl = config.OllamaUrl;
            if (baseUrl.Contains("/api/generate")) baseUrl = baseUrl.Replace("/api/generate", "");
            else if (baseUrl.EndsWith("/api")) baseUrl = baseUrl[..^4];
            
            var baseUri = baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/";
            var fullUrl = new Uri(new Uri(baseUri), "api/generate");
            var request = new { model = config.ModelName, prompt = "Say 'OK'", stream = false };
            var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");

            using var cts = new CancellationTokenSource(testTimeout);
            var response = await _httpClient.PostAsync(fullUrl, content, cts.Token);
            return (response.IsSuccessStatusCode, response.IsSuccessStatusCode ? "" : $"Ollama error ({response.StatusCode})");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public async Task<string> GenerateSqlAsync(int userId, string userPrompt)
    {
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        var sanitizedPrompt = userPrompt.Replace(";", "").Replace("--", "").Replace("/*", "").Replace("*/", "");
        var systemPrompt = GordonWorker.Prompts.SystemPrompts.GetSqlGenerationPrompt(today);
        var response = await GenerateCompletionAsync(userId, systemPrompt, sanitizedPrompt);
        if (response.StartsWith("I'm so sorry") || response.Contains("analytical engine") || response.Trim().StartsWith("SELECT 'Unauthorized'"))
            throw new InvalidOperationException("AI failed to generate a valid or authorized SQL query.");
        return response;
    }

    public async Task<string> FormatResponseAsync(int userId, string userPrompt, string dataContext, bool isWhatsApp = false, CancellationToken ct = default)
    {
        var settings = await _settingsService.GetSettingsAsync(userId);
        var formattingRule = isWhatsApp
            ? "4. **Formatting:** Use WhatsApp formatting: *bold* for bold, _italics_ for italics, and - for bullet points. Do NOT use HTML or standard Markdown bold (**)."
            : "4. **Formatting:** Use semantic HTML tags for Telegram: <b>bold</b> and <i>italic</i>. For lists, use plain bullet points (•). Do NOT use standard Markdown (**, _, ###).";

        var systemPrompt = GordonWorker.Prompts.SystemPrompts.GetFormatResponsePrompt(settings.SystemPersona, settings.UserName, formattingRule, dataContext);
        return await GenerateCompletionAsync(userId, systemPrompt, userPrompt, ct: ct, requiresReasoning: true);
    }

    public async Task<string> GenerateSimpleReportAsync(int userId, string statsJson)
    {
        var settings = await _settingsService.GetSettingsAsync(userId);
        var systemPrompt = GordonWorker.Prompts.SystemPrompts.GetWeeklyReportPrompt(settings.SystemPersona, settings.UserName);
        return await GenerateCompletionAsync(userId, systemPrompt, $"[DATA_CONTEXT]\n{statsJson}\n[/DATA_CONTEXT]\n\nResponse:", requiresReasoning: true);
    }

    public async Task<string> GenerateCompletionAsync(int userId, string system, string prompt, bool useFallback = false, CancellationToken ct = default, bool useThinking = false, bool requiresReasoning = false)
    {
        var settings = await _settingsService.GetSettingsAsync(userId);
        var perCallTimeout = settings.AiTimeoutSeconds > 0 ? settings.AiTimeoutSeconds : 90;
        using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadlineCts.CancelAfter(TimeSpan.FromSeconds(perCallTimeout * 3));
        var token = deadlineCts.Token;

        // Gate the thinking model so we only pay its latency when it actually helps.
        // Classifier / intent-detection / SQL-gen callers are simple JSON-shape tasks — a
        // reasoning preamble is pure overhead there. We invoke the thinking model only when:
        //   1. The caller explicitly opts in (requiresReasoning=true — e.g. FormatResponse,
        //      weekly/daily reports, affordability verdict), OR
        //   2. The prompt itself is long enough (>2000 chars) that strategic decomposition
        //      is likely to pay for itself (e.g. huge data contexts).
        // This cuts typical chat-reply latency roughly in half for simple QUERY intents.
        var finalPrompt = prompt;
        var shouldThink = settings.EnableThinkingModel
                          && !useThinking
                          && (requiresReasoning || prompt.Length > 2000);
        if (shouldThink)
        {
            try
            {
                var thinkingSystem = GordonWorker.Prompts.SystemPrompts.GetThinkingPrompt();
                var reasoning = await SendRawCompletionAsync(userId, thinkingSystem, prompt, useFallback: false, ct: token, useThinking: true);
                if (!string.IsNullOrWhiteSpace(reasoning))
                    finalPrompt = $"[REASONING_AND_STRATEGY]\n{reasoning}\n[/REASONING_AND_STRATEGY]\n\n[ORIGINAL_QUERY]\n{prompt}\n[/ORIGINAL_QUERY]";
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Thinking model failed."); }
        }

        var maxAttempts = Math.Max(1, settings.AiRetryAttempts);
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                return await SendRawCompletionAsync(userId, system, finalPrompt, useFallback, token, useThinking);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested || token.IsCancellationRequested)
            {
                // Caller asked us to stop (e.g. Telegram processing budget hit). Do NOT retry —
                // bubble it up so the caller can render the right user-facing message.
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AI attempt {Attempt} failed.", attempt);
                if (attempt == maxAttempts && !useFallback && settings.EnableAiFallback)
                {
                    _logger.LogWarning("Switching to fallback AI.");
                    return await GenerateCompletionAsync(userId, system, prompt, useFallback: true, ct: token, useThinking: useThinking, requiresReasoning: requiresReasoning);
                }
                if (attempt < maxAttempts) await Task.Delay(TimeSpan.FromSeconds(2 * attempt), token);
            }
        }

        return "I'm so sorry, but I'm having a bit of trouble connecting to my 'analytical engine' right now. Your financial data is perfectly safe and I'm still syncing your transactions in the background. Please try again in a few minutes, or double-check your AI settings on the dashboard.";
    }

    private async Task<string> SendRawCompletionAsync(int userId, string system, string prompt, bool useFallback = false, CancellationToken ct = default, bool useThinking = false)
    {
        var config = useThinking ? await GetThinkingProviderConfigAsync(userId) : await GetProviderConfigAsync(userId, useFallback);

        if (config.Provider == "Gemini") return await GenerateGeminiCompletionAsync(userId, system, prompt, config.GeminiKey, config.ModelName, ct, config.TimeoutSeconds);
        if (config.Provider == "OpenAI") return await GenerateOpenAiCompletionAsync(userId, system, prompt, config.OpenAiKey, config.ModelName, ct, config.TimeoutSeconds);
        if (config.Provider == "Anthropic") return await GenerateAnthropicCompletionAsync(userId, system, prompt, config.AnthropicKey, config.ModelName, ct, config.TimeoutSeconds);
        if (config.Provider == "Claude CLI") return await _claudeCliService.AskClaudeAsync($"{system}\n\n{prompt}", config.ModelName, config.ClaudeCliToken, ct);

        if (string.IsNullOrWhiteSpace(config.ModelName)) throw new InvalidOperationException("AI model is not configured.");

        var request = new { model = config.ModelName, prompt = $"{system}\n\n{prompt}", stream = false, keep_alive = -1 };
        var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");

        var baseUrl = config.OllamaUrl;
        if (baseUrl.Contains("/api/generate")) baseUrl = baseUrl.Replace("/api/generate", "");
        else if (baseUrl.EndsWith("/api")) baseUrl = baseUrl[..^4];

        var baseUri = baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/";
        var fullUrl = new Uri(new Uri(baseUri), "api/generate");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(config.TimeoutSeconds, 180)));

        _logger.LogInformation("Sending request to Ollama: {Url}, Model: {Model}", fullUrl, config.ModelName);
        var response = await _httpClient.PostAsync(fullUrl, content, cts.Token);
        response.EnsureSuccessStatusCode();
        var responseString = await response.Content.ReadAsStringAsync(ct);
        var result = JsonSerializer.Deserialize<OllamaResponse>(responseString);
        return result?.Response?.Trim() ?? throw new InvalidOperationException("Ollama returned empty response.");
    }

    private async Task<string> GenerateGeminiCompletionAsync(int userId, string system, string prompt, string apiKey, string modelName, CancellationToken ct = default, int timeoutSeconds = 60)
    {
        var model = !string.IsNullOrWhiteSpace(modelName) ? (modelName.StartsWith("models/") ? modelName[7..] : modelName) : "gemini-2.0-flash";
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent";
        var request = new
        {
            contents = new[] { new { role = "user", parts = new[] { new { text = system + "\n\n" + prompt } } } },
            safetySettings = new[] {
                new { category = "HARM_CATEGORY_HARASSMENT", threshold = "BLOCK_NONE" },
                new { category = "HARM_CATEGORY_HATE_SPEECH", threshold = "BLOCK_NONE" },
                new { category = "HARM_CATEGORY_SEXUALLY_EXPLICIT", threshold = "BLOCK_NONE" },
                new { category = "HARM_CATEGORY_DANGEROUS_CONTENT", threshold = "BLOCK_NONE" }
            }
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        using var httpReq = new HttpRequestMessage(HttpMethod.Post, url);
        httpReq.Headers.Add("x-goog-api-key", apiKey);
        httpReq.Content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
        
        var response = await _httpClient.SendAsync(httpReq, cts.Token);
        response.EnsureSuccessStatusCode();
        var responseString = await response.Content.ReadAsStringAsync(cts.Token);
        using var doc = JsonDocument.Parse(responseString);
        return doc.RootElement.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString()?.Trim() ?? "";
    }

    private async Task<string> GenerateOpenAiCompletionAsync(int userId, string system, string prompt, string apiKey, string modelName, CancellationToken ct = default, int timeoutSeconds = 90)
    {
        var request = new { model = !string.IsNullOrWhiteSpace(modelName) ? modelName : "gpt-4o-mini", messages = new[] { new { role = "system", content = system }, new { role = "user", content = prompt } } };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        req.Content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
        var response = await _httpClient.SendAsync(req, cts.Token);
        response.EnsureSuccessStatusCode();
        var responseString = await response.Content.ReadAsStringAsync(cts.Token);
        using var doc = JsonDocument.Parse(responseString);
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()?.Trim() ?? "";
    }

    private async Task<string> GenerateAnthropicCompletionAsync(int userId, string system, string prompt, string apiKey, string modelName, CancellationToken ct = default, int timeoutSeconds = 90)
    {
        var request = new { model = !string.IsNullOrWhiteSpace(modelName) ? modelName : "claude-3-5-sonnet-latest", max_tokens = 8192, system, messages = new[] { new { role = "user", content = prompt } } };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
        req.Headers.Add("x-api-key", apiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");
        req.Content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
        var response = await _httpClient.SendAsync(req, cts.Token);
        response.EnsureSuccessStatusCode();
        var responseString = await response.Content.ReadAsStringAsync(cts.Token);
        using var doc = JsonDocument.Parse(responseString);
        return doc.RootElement.GetProperty("content")[0].GetProperty("text").GetString()?.Trim() ?? "";
    }

    private class AiProviderConfig
    {
        public string Provider { get; set; } = "Ollama";
        public string OllamaUrl { get; set; } = "";
        public string ModelName { get; set; } = "";
        public string GeminiKey { get; set; } = "";
        public string OpenAiKey { get; set; } = "";
        public string AnthropicKey { get; set; } = "";
        public string ClaudeCliToken { get; set; } = "";
        public int TimeoutSeconds { get; set; } = 90;
        public int RetryAttempts { get; set; } = 2;
    }

    private class OllamaResponse { [System.Text.Json.Serialization.JsonPropertyName("response")] public string? Response { get; set; } }
    private class OllamaTagsResponse { [System.Text.Json.Serialization.JsonPropertyName("models")] public List<OllamaModelTag>? Models { get; set; } }
    private class OllamaModelTag { [System.Text.Json.Serialization.JsonPropertyName("name")] public string? Name { get; set; } }
}
