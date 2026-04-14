using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using GordonWorker.Models;
using System.Text;
using System.Globalization;

namespace GordonWorker.Services;

public interface IInvestecClient
{
    void Configure(string clientId, string secret, string apiKey, string baseUrl = "https://openapi.investec.com/", string environment = "Production");
    Task<string> AuthenticateAsync();
    Task<List<InvestecAccount>> GetAccountsAsync();
    Task<List<Transaction>> GetTransactionsAsync(string accountId, DateTimeOffset fromDate);
    Task<(bool Success, string Error)> TestConnectivityAsync();
    Task<decimal> GetAccountBalanceAsync(string accountId);
    Task<List<InvestecCard>> GetCardsAsync(string accountId);
    Task<(bool Success, string Error)> ExecuteTransferAsync(string fromAccountId, string toAccountId, decimal amount, string reference, bool isDryRun);
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

public class InvestecCard
{
    public string CardKey { get; set; } = string.Empty;
    public string CardNumber { get; set; } = string.Empty;
    public bool IsProgrammable { get; set; }
    public string Status { get; set; } = string.Empty;
}

public class InvestecClient : IInvestecClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<InvestecClient> _logger;
    private readonly SemaphoreSlim _authLock = new(1, 1);

    // All per-tenant state lives in this immutable session record. Configure() atomically swaps
    // the entire reference, so any method that captures it once at the top is guaranteed to see
    // a self-consistent set of credentials + token + cache, even if Configure() is called again
    // concurrently (e.g. by another scope sharing the same instance by accident). This eliminates
    // an entire class of cross-tenant credential-leak bugs.
    private sealed class Session
    {
        public string ClientId { get; init; } = string.Empty;
        public string Secret { get; init; } = string.Empty;
        public string ApiKey { get; init; } = string.Empty;
        public string BaseUrl { get; init; } = "https://openapi.investec.com/";
        public string? AccessToken;          // mutable but only mutated under _authLock
        public DateTime TokenExpiry;         // ditto
        public List<InvestecAccount> CachedAccounts = new();
    }

    private volatile Session? _session;

    public InvestecClient(HttpClient httpClient, ILogger<InvestecClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public void Configure(string clientId, string secret, string apiKey, string baseUrl = "https://openapi.investec.com/", string environment = "Production")
    {
        var effectiveUrl = string.IsNullOrWhiteSpace(baseUrl) ? "https://openapi.investec.com/" : baseUrl;
        effectiveUrl = effectiveUrl.TrimEnd('/') + "/";
        if (environment.Equals("Sandbox", StringComparison.OrdinalIgnoreCase) && !effectiveUrl.Contains("sandbox", StringComparison.OrdinalIgnoreCase))
        {
            effectiveUrl += "sandbox/";
        }

        // Atomic single-reference swap. Any in-flight call that already captured the previous
        // session keeps using its own credentials to completion — which is the correct behaviour.
        _session = new Session
        {
            ClientId = clientId ?? string.Empty,
            Secret = secret ?? string.Empty,
            ApiKey = apiKey ?? string.Empty,
            BaseUrl = effectiveUrl
        };
    }

    private Session RequireSession()
    {
        var s = _session;
        if (s == null)
            throw new InvalidOperationException("InvestecClient.Configure() must be called before any API method.");
        return s;
    }

    private static Uri GetUri(Session session, string path) => new Uri(new Uri(session.BaseUrl), path);

    public async Task<decimal> GetAccountBalanceAsync(string accountId)
    {
        var session = RequireSession();
        if (string.IsNullOrEmpty(session.AccessToken) || DateTime.UtcNow > session.TokenExpiry) await AuthenticateAsync();

        // Find account type from cache or fetch if missing
        if (!session.CachedAccounts.Any()) await GetAccountsAsync();
        var account = session.CachedAccounts.FirstOrDefault(a => a.AccountId == accountId);

        var request = new HttpRequestMessage(HttpMethod.Get, GetUri(session, $"za/pb/v1/accounts/{accountId}/balance"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
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
        var session = RequireSession();
        if (string.IsNullOrEmpty(session.ClientId) || string.IsNullOrEmpty(session.Secret)) return string.Empty;

        await _authLock.WaitAsync();
        try
        {
            // Double-check after acquiring lock — another thread may have refreshed this session.
            if (!string.IsNullOrEmpty(session.AccessToken) && DateTime.UtcNow <= session.TokenExpiry)
                return session.AccessToken;

            var request = new HttpRequestMessage(HttpMethod.Post, GetUri(session, "identity/v2/oauth2/token"));
            var authString = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{session.ClientId}:{session.Secret}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", authString);
            request.Headers.Add("x-api-key", session.ApiKey);
            request.Content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("grant_type", "client_credentials") });
            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode) return string.Empty;
            var content = await response.Content.ReadAsStringAsync();
            var tokenResponse = JsonSerializer.Deserialize<JsonElement>(content);
            session.AccessToken = tokenResponse.GetProperty("access_token").GetString();
            var expiresIn = tokenResponse.GetProperty("expires_in").GetInt32();
            session.TokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn - 60);
            return session.AccessToken ?? string.Empty;
        }
        finally
        {
            _authLock.Release();
        }
    }

    public async Task<List<InvestecAccount>> GetAccountsAsync()
    {
        var session = RequireSession();
        if (string.IsNullOrEmpty(session.AccessToken) || DateTime.UtcNow > session.TokenExpiry) await AuthenticateAsync();
        var request = new HttpRequestMessage(HttpMethod.Get, GetUri(session, "za/pb/v1/accounts"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode) return new List<InvestecAccount>();
        var content = await response.Content.ReadAsStringAsync();
        var root = JsonSerializer.Deserialize<InvestecAccountsResponse>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        session.CachedAccounts = root?.Data?.Accounts ?? new List<InvestecAccount>();
        return session.CachedAccounts;
    }

    public async Task<List<Transaction>> GetTransactionsAsync(string accountId, DateTimeOffset fromDate)
    {
        var session = RequireSession();
        if (string.IsNullOrEmpty(session.AccessToken) || DateTime.UtcNow > session.TokenExpiry) await AuthenticateAsync();

        var transactions = new List<Transaction>();
        var url = $"za/pb/v1/accounts/{accountId}/transactions?fromDate={fromDate:yyyy-MM-dd}";

        while (!string.IsNullOrEmpty(url))
        {
            var request = new HttpRequestMessage(HttpMethod.Get, GetUri(session, url));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode) break;
            
            var content = await response.Content.ReadAsStringAsync();
            var root = JsonSerializer.Deserialize<InvestecResponse>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            
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
            
            var nextUrl = root?.Links?.Next;
            if (!string.IsNullOrEmpty(nextUrl))
            {
                url = nextUrl.TrimStart('/'); 
            }
            else
            {
                url = string.Empty;
            }
        }
        
        return transactions;
    }

    public async Task<List<InvestecCard>> GetCardsAsync(string accountId)
    {
        var session = RequireSession();
        if (string.IsNullOrEmpty(session.AccessToken) || DateTime.UtcNow > session.TokenExpiry) await AuthenticateAsync();
        var request = new HttpRequestMessage(HttpMethod.Get, GetUri(session, $"za/pb/v1/accounts/{accountId}/cards"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode) return new List<InvestecCard>();
        var content = await response.Content.ReadAsStringAsync();
        var root = JsonSerializer.Deserialize<InvestecCardsResponse>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return root?.Data?.Cards ?? new List<InvestecCard>();
    }

    public async Task<(bool Success, string Error)> ExecuteTransferAsync(string fromAccountId, string toAccountId, decimal amount, string reference, bool isDryRun)
    {
        if (isDryRun)
        {
            _logger.LogInformation("[DRY RUN] Would execute transfer of R{Amount:F2} from {From} to {To} with reference '{Ref}'", amount, fromAccountId, toAccountId, reference);
            return (true, string.Empty);
        }

        var session = RequireSession();
        if (string.IsNullOrEmpty(session.AccessToken) || DateTime.UtcNow > session.TokenExpiry) await AuthenticateAsync();

        var transferPayload = new
        {
            transferList = new[]
            {
                new
                {
                    beneficiaryAccountId = toAccountId,
                    amount = amount.ToString("F2", CultureInfo.InvariantCulture),
                    myReference = reference,
                    theirReference = reference
                }
            }
        };

        var request = new HttpRequestMessage(HttpMethod.Post, GetUri(session, $"za/pb/v1/accounts/{fromAccountId}/transfermultiple"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        request.Content = new StringContent(JsonSerializer.Serialize(transferPayload), Encoding.UTF8, "application/json");

        try
        {
            var response = await _httpClient.SendAsync(request);
            var content = await response.Content.ReadAsStringAsync();
            
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Successfully executed transfer of R{Amount:F2} from {From} to {To}", amount, fromAccountId, toAccountId);
                return (true, string.Empty);
            }
            else
            {
                _logger.LogError("Failed to execute transfer. Status: {Status}, Content: {Content}", response.StatusCode, content);
                return (false, $"Investec API Error: {response.StatusCode} - {content}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception while executing transfer.");
            return (false, ex.Message);
        }
    }

    private Guid GenerateUuidFromString(string input) { using (var md5 = System.Security.Cryptography.MD5.Create()) { return new Guid(md5.ComputeHash(Encoding.UTF8.GetBytes(input))); } }
    private class InvestecAccountsResponse { public InvestecAccountsData? Data { get; set; } }
    private class InvestecAccountsData { public List<InvestecAccount>? Accounts { get; set; } }
    private class InvestecResponse { public InvestecData? Data { get; set; } public InvestecLinks? Links { get; set; } }
    private class InvestecLinks { public string? Next { get; set; } }
    private class InvestecData { public List<InvestecTransaction>? Transactions { get; set; } }
    private class InvestecTransaction { public string? Id { get; set; } public string? Description { get; set; } public decimal Amount { get; set; } public decimal AccountBalance { get; set; } public DateTimeOffset TransactionDate { get; set; } public string? Type { get; set; } }
    private class InvestecCardsResponse { public InvestecCardsData? Data { get; set; } }
    private class InvestecCardsData { public List<InvestecCard>? Cards { get; set; } }
}
