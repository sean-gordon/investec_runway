using Dapper;
using GordonWorker.Models;
using GordonWorker.Services;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using System.Text.Json;

namespace GordonWorker.Controllers;

[ApiController]
[Route("[controller]")]
public class ChatController : ControllerBase
{
    private readonly IOllamaService _ollamaService;
    private readonly IActuarialService _actuarialService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ChatController> _logger;

    public ChatController(IOllamaService ollamaService, IActuarialService actuarialService, IConfiguration configuration, ILogger<ChatController> logger)
    {
        _ollamaService = ollamaService;
        _actuarialService = actuarialService;
        _configuration = configuration;
        _logger = logger;
    }

    [HttpPost]
    public async Task<IActionResult> Chat([FromBody] ChatRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
        {
            return BadRequest("Message cannot be empty.");
        }

        string dataContext;
        var lowerMessage = request.Message.ToLowerInvariant();

        using var connection = new NpgsqlConnection(_configuration.GetConnectionString("DefaultConnection"));
        await connection.OpenAsync();

        // Check for specific "Deep Analysis" intents
        var analysisKeywords = new[] { "runway", "burn", "forecast", "health", "analysis", "prediction", "risk" };
        if (analysisKeywords.Any(k => lowerMessage.Contains(k)))
        {
            // 1. Fetch Raw Data for the Actuary Engine (Last 90 Days)
            var sqlHistory = @"
                SELECT * FROM transactions 
                WHERE transaction_date >= NOW() - INTERVAL '90 days'
                ORDER BY transaction_date ASC";
            
            var history = (await connection.QueryAsync<Transaction>(sqlHistory)).ToList();

            // 2. Fetch Current Balance (Latest)
            var sqlBalance = "SELECT balance FROM transactions ORDER BY transaction_date DESC LIMIT 1";
            var currentBalance = await connection.ExecuteScalarAsync<decimal?>(sqlBalance) ?? 0;

            // 3. Run Actuarial Analysis
            var report = await _actuarialService.AnalyzeHealthAsync(history, currentBalance);

            // 4. Create Context
            dataContext = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            
            _logger.LogInformation("Actuarial Report Generated: {Report}", dataContext);
        }
        else
        {
            // Text-to-SQL Agent for general queries
            var sql = await _ollamaService.GenerateSqlAsync(request.Message);
            _logger.LogInformation("Generated SQL: {Sql}", sql);

            try
            {
                if (!sql.Trim().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
                {
                    dataContext = "Error: The AI generated an invalid query (non-SELECT).";
                }
                else
                {
                    var result = await connection.QueryAsync(sql);
                    dataContext = JsonSerializer.Serialize(result);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing generated SQL.");
                dataContext = $"Error executing query: {ex.Message}";
            }
        }

        var finalResponse = await _ollamaService.FormatResponseAsync(request.Message, dataContext);
        return Ok(new { Response = finalResponse });
    }
}

public record ChatRequest(string Message);
