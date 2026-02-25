using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using GordonWorker.Models;
using System.Text;
using System.Globalization;

namespace GordonWorker.Services;

public interface IInvestecClient
{
    void Configure(string clientId, string secret, string apiKey, string baseUrl = "https://openapi.investec.com/");
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
    public string ProductPath { get; set; } = string.Empty;
    
    public bool IsLiability => ProductPath.Contains("Credit Card", StringComparison.OrdinalIgnoreCase) || 
                               ProductPath.Contains("Loan", StringComparison.OrdinalIgnoreCase);
}

public class InvestecClient : IInvestecClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<InvestecClient> _logger;
    
    private string? _clientId;
    private string? _secret;
    private string? _apiKey;
    private string? _accessToken;
    private string _baseUrl = "https://openapi.investec.com/";
    private DateTime _tokenExpiry;
    private List<InvestecAccount> _cachedAccounts = new();

    public InvestecClient(HttpClient httpClient, ILogger<InvestecClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public void Configure(string clientId, string secret, string apiKey, string baseUrl = "https://openapi.investec.com/")
    {
        _clientId = clientId;
        _secret = secret;
        _apiKey = apiKey;
        var effectiveUrl = string.IsNullOrWhiteSpace(baseUrl) ? "https://openapi.investec.com/" : baseUrl;
        _baseUrl = effectiveUrl.EndsWith("/") ? effectiveUrl : effectiveUrl + "/";
        _accessToken = null; // Reset token on reconfig
        _cachedAccounts.Clear();
    }

    private Uri GetUri(string path) => new Uri(new Uri(_baseUrl), path);

    public async Task<decimal> GetAccountBalanceAsync(string accountId)
    {
        if (string.IsNullOrEmpty(_accessToken) || DateTime.UtcNow > _tokenExpiry) await AuthenticateAsync();
        
        // Find account type from cache or fetch if missing
        if (!_cachedAccounts.Any()) await GetAccountsAsync();
        var account = _cachedAccounts.FirstOrDefault(a => a.AccountId == accountId);

        var request = new HttpRequestMessage(HttpMethod.Get, GetUri($"za/pb/v1/accounts/{accountId}/balance"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode) return 0;
        
        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        if (doc.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("currentBalance", out var currentBalance)) 
        {
            var val = currentBalance.GetDecimal();
            // If it's a liability (Credit Card), Investec returns amount owed as POSITIVE. 
            // We want to return it as NEGATIVE to represent debt in the total sum.
            if (account != null && account.IsLiability && val > 0)
            {
                return -val;
            }
            return val;
        }
        return 0;
    }

    public async Task<(bool Success, string Error)> TestConnectivityAsync()
    {
        try { var token = await AuthenticateAsync(); return (!string.IsNullOrEmpty(token), string.Empty); }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public async Task<string> AuthenticateAsync()
    {
        if (string.IsNullOrEmpty(_clientId) || string.IsNullOrEmpty(_secret)) return string.Empty;
        
        var request = new HttpRequestMessage(HttpMethod.Post, GetUri("identity/v2/oauth2/token"));
        var authString = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_clientId}:{_secret}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", authString);
        request.Headers.Add("x-api-key", _apiKey);
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
        if (string.IsNullOrEmpty(_accessToken) || DateTime.UtcNow > _tokenExpiry) await AuthenticateAsync();
        var request = new HttpRequestMessage(HttpMethod.Get, GetUri("za/pb/v1/accounts"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode) return new List<InvestecAccount>();
        var content = await response.Content.ReadAsStringAsync();
        var root = JsonSerializer.Deserialize<InvestecAccountsResponse>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        _cachedAccounts = root?.Data?.Accounts ?? new List<InvestecAccount>();
        return _cachedAccounts;
    }

    public async Task<List<Transaction>> GetTransactionsAsync(string accountId, DateTimeOffset fromDate)
    {
        if (string.IsNullOrEmpty(_accessToken) || DateTime.UtcNow > _tokenExpiry) await AuthenticateAsync();
        var url = $"za/pb/v1/accounts/{accountId}/transactions?fromDate={fromDate:yyyy-MM-dd}";
        var request = new HttpRequestMessage(HttpMethod.Get, GetUri(url));
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
                    var hashKey = $"{accountId}_{t.TransactionDate:O}_{t.Description}_{t.Amount.ToString("F2", CultureInfo.InvariantCulture)}_{t.AccountBalance.ToString("F2", CultureInfo.InvariantCulture)}";
                    id = GenerateUuidFromString(hashKey);
                }
                var amount = t.Amount;
                // Defensive sign enforcement: Force Debit to be negative and Credit to be positive 
                // regardless of what the native API returns, to align with Gordon Engine's standard.
                if (string.Equals(t.Type, "DEBIT", StringComparison.OrdinalIgnoreCase))
                {
                    amount = -Math.Abs(amount);
                }
                else if (string.Equals(t.Type, "CREDIT", StringComparison.OrdinalIgnoreCase))
                {
                    amount = Math.Abs(amount);
                }

                transactions.Add(new Transaction 
                { 
                    Id = id, 
                    AccountId = accountId, 
                    TransactionDate = t.TransactionDate == default ? DateTimeOffset.UtcNow : t.TransactionDate.ToUniversalTime(), 
                    Description = t.Description, 
                    Amount = amount, 
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
