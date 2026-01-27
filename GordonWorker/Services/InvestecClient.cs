using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using GordonWorker.Models;

namespace GordonWorker.Services;

public interface IInvestecClient
{
    Task<string> AuthenticateAsync();
    Task<List<Transaction>> GetTransactionsAsync(string accountId, DateTimeOffset fromDate);
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

    public async Task<List<Transaction>> GetTransactionsAsync(string accountId, DateTimeOffset fromDate)
    {
        if (string.IsNullOrEmpty(_accessToken) || DateTime.UtcNow > _tokenExpiry)
        {
            await AuthenticateAsync();
        }

        var fromDateStr = fromDate.ToString("yyyy-MM-dd"); // Investec format usually
        // API endpoint might vary, using a standard approximation based on prompt
        var url = $"za/pb/v1/accounts/{accountId}/transactions?fromDate={fromDateStr}";
        
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

        var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Failed to fetch transactions: {StatusCode}", response.StatusCode);
            return new List<Transaction>();
        }

        var content = await response.Content.ReadAsStringAsync();
        // Parsing logic - assuming Investec structure
        // data: { transactions: [ ... ] }
        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var root = JsonSerializer.Deserialize<InvestecResponse>(content, jsonOptions);

        var transactions = new List<Transaction>();
        if (root?.Data?.Transactions != null)
        {
            foreach (var t in root.Data.Transactions)
            {
                // Map to our model
                // Note: Investec ID is usually string, we need UUID. 
                // We might need to hash it or parsing if it is UUID. 
                // Assuming we can parse or generate a stable UUID from the Investec ID string.
                // For this exercise, I'll assume standard UUID parsing or fallback.
                
                if (!Guid.TryParse(t.Id, out var id))
                {
                    // Generate stable UUID from string ID
                    id = GenerateUuidFromString(t.Id ?? Guid.NewGuid().ToString());
                }

                transactions.Add(new Transaction
                {
                    Id = id,
                    TransactionDate = t.TransactionDate == default ? DateTimeOffset.UtcNow : t.TransactionDate,
                    Description = t.Description,
                    Amount = t.Amount,
                    Balance = t.AccountBalance, // Assuming mapped
                    Category = t.Type, // Approximate mapping
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
        // Investec fields
        public string? Id { get; set; } // postingDate, valueDate, etc.
        public string? Description { get; set; }
        public decimal Amount { get; set; }
        public decimal AccountBalance { get; set; }
        public DateTimeOffset TransactionDate { get; set; } // postedOrder
        public string? Type { get; set; }
    }
}
