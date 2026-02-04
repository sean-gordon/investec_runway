using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using GordonWorker.Models;
using System.Text;
using System.Globalization;

namespace GordonWorker.Services;

public interface IInvestecClient
{
    Task<string> AuthenticateAsync();
    Task<List<InvestecAccount>> GetAccountsAsync();
    Task<List<Transaction>> GetTransactionsAsync(string accountId, DateTimeOffset fromDate);
    Task<(bool Success, string Error)> TestConnectivityAsync();
    Task<decimal> GetAccountBalanceAsync(string accountId);
}

public class InvestecAccount
{
    public string AccountId { get; set; } = string.Empty;
    public string AccountNumber { get; set; } = string.Empty;
    public string AccountName { get; set; } = string.Empty;
}

public class InvestecClient : IInvestecClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<InvestecClient> _logger;
    private readonly IConfiguration _configuration;
    private readonly ISettingsService _settingsService;
    private string? _accessToken;
    private DateTime _tokenExpiry;

    public InvestecClient(HttpClient httpClient, ILogger<InvestecClient> logger, IConfiguration configuration, ISettingsService settingsService)
    {
        _httpClient = httpClient;
        _logger = logger;
        _configuration = configuration;
        _settingsService = settingsService;
    }

    private async Task EnsureBaseAddressAsync()
    {
        if (_httpClient.BaseAddress == null)
        {
            var settings = await _settingsService.GetSettingsAsync();
            _httpClient.BaseAddress = new Uri(settings.InvestecBaseUrl);
        }
    }

    public async Task<decimal> GetAccountBalanceAsync(string accountId)
    {
        await EnsureBaseAddressAsync();
        if (string.IsNullOrEmpty(_accessToken) || DateTime.UtcNow > _tokenExpiry) await AuthenticateAsync();
        var request = new HttpRequestMessage(HttpMethod.Get, $"za/pb/v1/accounts/{accountId}/balance");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode) return 0;
        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        if (doc.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("currentBalance", out var currentBalance)) return currentBalance.GetDecimal();
        return 0;
    }

    public async Task<(bool Success, string Error)> TestConnectivityAsync()
    {
        try { var token = await AuthenticateAsync(); return (!string.IsNullOrEmpty(token), string.Empty); }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public async Task<string> AuthenticateAsync()
    {
        await EnsureBaseAddressAsync();
        var settings = await _settingsService.GetSettingsAsync();
        var clientId = settings.InvestecClientId;
        var secret = settings.InvestecSecret;
        var apiKey = settings.InvestecApiKey;

        // Fallback to configuration if DB settings are empty (legacy support/initial setup)
        if (string.IsNullOrEmpty(clientId)) clientId = _configuration["INVESTEC_CLIENT_ID"];
        if (string.IsNullOrEmpty(secret)) secret = _configuration["INVESTEC_SECRET"];
        if (string.IsNullOrEmpty(apiKey)) apiKey = _configuration["INVESTEC_API_KEY"];

        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(secret)) return string.Empty;
        
        var request = new HttpRequestMessage(HttpMethod.Post, "identity/v2/oauth2/token");
        var authString = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{secret}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", authString);
        request.Headers.Add("x-api-key", apiKey);
        request.Content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("grant_type", "client_credentials") });
        var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode) return string.Empty;
        var content = await response.Content.ReadAsStringAsync();
        var tokenResponse = JsonSerializer.Deserialize<JsonElement>(content);
        _accessToken = tokenResponse.GetProperty("access_token").GetString();
        var expiresIn = tokenResponse.GetProperty("expires_in").GetInt32();
        _tokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn - 60);
        return _accessToken ?? string.Empty;
    }

    public async Task<List<InvestecAccount>> GetAccountsAsync()
    {
        await EnsureBaseAddressAsync();
        if (string.IsNullOrEmpty(_accessToken) || DateTime.UtcNow > _tokenExpiry) await AuthenticateAsync();
        var request = new HttpRequestMessage(HttpMethod.Get, "za/pb/v1/accounts");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode) return new List<InvestecAccount>();
        var content = await response.Content.ReadAsStringAsync();
        var root = JsonSerializer.Deserialize<InvestecAccountsResponse>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return root?.Data?.Accounts ?? new List<InvestecAccount>();
    }

    public async Task<List<Transaction>> GetTransactionsAsync(string accountId, DateTimeOffset fromDate)
    {
        await EnsureBaseAddressAsync();
        if (string.IsNullOrEmpty(_accessToken) || DateTime.UtcNow > _tokenExpiry) await AuthenticateAsync();
        var url = $"za/pb/v1/accounts/{accountId}/transactions?fromDate={fromDate:yyyy-MM-dd}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode) return new List<Transaction>();
        var content = await response.Content.ReadAsStringAsync();
        var root = JsonSerializer.Deserialize<InvestecResponse>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        var transactions = new List<Transaction>();
        if (root?.Data?.Transactions != null)
        {
            foreach (var t in root.Data.Transactions)
            {
                if (!Guid.TryParse(t.Id, out var id))
                {
                    // CRITICAL FIX: Use InvariantCulture for stable IDs across re-syncs
                    var hashKey = $"{accountId}_{t.TransactionDate:O}_{t.Description}_{t.Amount.ToString("F2", CultureInfo.InvariantCulture)}_{t.AccountBalance.ToString("F2", CultureInfo.InvariantCulture)}";
                    id = GenerateUuidFromString(hashKey);
                }
                transactions.Add(new Transaction 
                { 
                    Id = id, 
                    AccountId = accountId, 
                    TransactionDate = t.TransactionDate == default ? DateTimeOffset.UtcNow : t.TransactionDate.ToUniversalTime(), 
                    Description = t.Description, 
                    Amount = t.Amount, 
                    Balance = t.AccountBalance, 
                    Category = t.Type, 
                    IsAiProcessed = false 
                });
            }
        }
        return transactions;
    }

    private Guid GenerateUuidFromString(string input) { using (var md5 = System.Security.Cryptography.MD5.Create()) { return new Guid(md5.ComputeHash(Encoding.UTF8.GetBytes(input))); } }
    private class InvestecAccountsResponse { public InvestecAccountsData? Data { get; set; } }
    private class InvestecAccountsData { public List<InvestecAccount>? Accounts { get; set; } }
    private class InvestecResponse { public InvestecData? Data { get; set; } }
    private class InvestecData { public List<InvestecTransaction>? Transactions { get; set; } }
    private class InvestecTransaction { public string? Id { get; set; } public string? Description { get; set; } public decimal Amount { get; set; } public decimal AccountBalance { get; set; } public DateTimeOffset TransactionDate { get; set; } public string? Type { get; set; } }
}
