using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using GordonWorker.Models;

namespace GordonWorker.Services;

public interface IInvestecClient
{
    Task<string> AuthenticateAsync();
    Task<List<InvestecAccount>> GetAccountsAsync();
    Task<List<Transaction>> GetTransactionsAsync(string accountId, DateTimeOffset fromDate);
    Task<(bool Success, string Error)> TestConnectivityAsync();
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
    private string? _accessToken;
    private DateTime _tokenExpiry;

    public InvestecClient(HttpClient httpClient, ILogger<InvestecClient> logger, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _logger = logger;
        _configuration = configuration;
        _httpClient.BaseAddress = new Uri("https://openapi.investec.com/");
    }

    public async Task<(bool Success, string Error)> TestConnectivityAsync()
    {
        try 
        {
            var token = await AuthenticateAsync();
            if (string.IsNullOrEmpty(token)) return (false, "Authentication failed (Empty Token). Check Credentials.");
            return (true, string.Empty);
        }
        catch (HttpRequestException httpEx)
        {
            _logger.LogError(httpEx, "Investec HTTP error.");
            return (false, $"HTTP Error: {httpEx.StatusCode} - {httpEx.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Investec connectivity check failed.");
            return (false, $"Error: {ex.Message}");
        }
    }

    public async Task<string> AuthenticateAsync()
    {
        var clientId = _configuration["INVESTEC_CLIENT_ID"];
        var secret = _configuration["INVESTEC_SECRET"];
        var apiKey = _configuration["INVESTEC_API_KEY"];

        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(secret))
        {
            _logger.LogWarning("Investec credentials missing.");
            return string.Empty;
        }

        var request = new HttpRequestMessage(HttpMethod.Post, "identity/v2/oauth2/token");
        var authString = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($"{clientId}:{secret}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", authString);
        request.Headers.Add("x-api-key", apiKey);
        
        request.Content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("grant_type", "client_credentials")
        });

        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync();
        var tokenResponse = JsonSerializer.Deserialize<JsonElement>(content);
        
        _accessToken = tokenResponse.GetProperty("access_token").GetString();
        var expiresIn = tokenResponse.GetProperty("expires_in").GetInt32();
        _tokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn - 60); // Buffer

        return _accessToken ?? string.Empty;
    }

    public async Task<List<InvestecAccount>> GetAccountsAsync()
    {
        if (string.IsNullOrEmpty(_accessToken) || DateTime.UtcNow > _tokenExpiry)
        {
            await AuthenticateAsync();
        }

        var request = new HttpRequestMessage(HttpMethod.Get, "za/pb/v1/accounts");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

        var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Failed to fetch accounts: {StatusCode}", response.StatusCode);
            return new List<InvestecAccount>();
        }

        var content = await response.Content.ReadAsStringAsync();
        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var root = JsonSerializer.Deserialize<InvestecAccountsResponse>(content, jsonOptions);

        return root?.Data?.Accounts ?? new List<InvestecAccount>();
    }

    public async Task<List<Transaction>> GetTransactionsAsync(string accountId, DateTimeOffset fromDate)
    {
        if (string.IsNullOrEmpty(_accessToken) || DateTime.UtcNow > _tokenExpiry)
        {
            await AuthenticateAsync();
        }

        var fromDateStr = fromDate.ToString("yyyy-MM-dd"); 
        var url = $"za/pb/v1/accounts/{accountId}/transactions?fromDate={fromDateStr}";
        
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

        var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Failed to fetch transactions for account {AccountId}: {StatusCode}", accountId, response.StatusCode);
            return new List<Transaction>();
        }

        var content = await response.Content.ReadAsStringAsync();
        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var root = JsonSerializer.Deserialize<InvestecResponse>(content, jsonOptions);

        var transactions = new List<Transaction>();
        if (root?.Data?.Transactions != null)
        {
            foreach (var t in root.Data.Transactions)
            {
                if (!Guid.TryParse(t.Id, out var id))
                {
                    id = GenerateUuidFromString(t.Id ?? Guid.NewGuid().ToString());
                }

                transactions.Add(new Transaction
                {
                    Id = id,
                    AccountId = accountId,
                    TransactionDate = t.TransactionDate == default ? DateTimeOffset.UtcNow : t.TransactionDate,
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

    private Guid GenerateUuidFromString(string input)
    {
        using (var md5 = System.Security.Cryptography.MD5.Create())
        {
            var hash = md5.ComputeHash(System.Text.Encoding.Default.GetBytes(input));
            return new Guid(hash);
        }
    }

    private class InvestecAccountsResponse
    {
        public InvestecAccountsData? Data { get; set; }
    }

    private class InvestecAccountsData
    {
        public List<InvestecAccount>? Accounts { get; set; }
    }

    private class InvestecResponse
    {
        public InvestecData? Data { get; set; }
    }

    private class InvestecData
    {
        public List<InvestecTransaction>? Transactions { get; set; }
    }

    private class InvestecTransaction
    {
        public string? Id { get; set; } 
        public string? Description { get; set; }
        public decimal Amount { get; set; }
        public decimal AccountBalance { get; set; }
        public DateTimeOffset TransactionDate { get; set; } 
        public string? Type { get; set; }
    }
}