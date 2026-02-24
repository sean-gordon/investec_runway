using GordonWorker.Services;
using GordonWorker.Workers;
using GordonWorker.Middleware;
using GordonWorker.Infrastructure;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using Dapper;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

// Support legacy timestamp behavior for Npgsql
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
AppContext.SetSwitch("Npgsql.DisableDateTimeInfinityConversions", true);

var builder = WebApplication.CreateBuilder(args);

// Enable snake_case mapping for Dapper
DefaultTypeMap.MatchNamesWithUnderscores = true;

// JWT Authentication Setup with Security Validation
var jwtSettings = builder.Configuration.GetSection("Jwt");
var jwtSecret = jwtSettings["Secret"];

// SECURITY FIX: Validate JWT secret at startup
if (string.IsNullOrWhiteSpace(jwtSecret) || jwtSecret == "PLEASE_CHANGE_THIS_TO_A_VERY_LONG_RANDOM_STRING_IN_PRODUCTION")
{
    throw new InvalidOperationException(
        "CRITICAL SECURITY ERROR: JWT Secret is not configured or uses default value. " +
        "Set a strong JWT secret in appsettings.json or environment variables before starting the application.");
}

if (jwtSecret.Length < 32)
{
    throw new InvalidOperationException(
        "CRITICAL SECURITY ERROR: JWT Secret must be at least 32 characters long for adequate security.");
}

var key = Encoding.ASCII.GetBytes(jwtSecret);

builder.Services.AddAuthentication(x =>
{
    x.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    x.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(x =>
{
    // SECURITY FIX: RequireHttpsMetadata should be true in production
    x.RequireHttpsMetadata = builder.Environment.IsProduction();
    x.SaveToken = true;
    x.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(key),
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidIssuer = jwtSettings["Issuer"],
        ValidAudience = jwtSettings["Audience"],
        ClockSkew = TimeSpan.Zero
    };
});

builder.Services.AddLogging(logging =>
{
    logging.AddConsole();
    var serviceProvider = builder.Services.BuildServiceProvider();
    var sink = serviceProvider.GetRequiredService<ILogSinkService>();
    logging.AddProvider(new InMemoryLoggerProvider(sink));
});

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo("keys"));

// Memory cache for settings
builder.Services.AddMemoryCache();

// ENHANCEMENT: Add rate limiting
builder.Services.AddRateLimiter(options =>
{
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        var user = context.User.Identity?.Name ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous";

        return RateLimitPartition.GetFixedWindowLimiter(user, partition => new FixedWindowRateLimiterOptions
        {
            AutoReplenishment = true,
            PermitLimit = 100,
            Window = TimeSpan.FromMinutes(1)
        });
    });

    options.RejectionStatusCode = 429;
});

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

// Service Registration
builder.Services.AddHttpClient<IInvestecClient, InvestecClient>();
builder.Services.AddHttpClient<IAiService, AiService>()
    .SetHandlerLifetime(TimeSpan.FromMinutes(5))
    .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromMinutes(5));

builder.Services.AddSingleton<ISettingsService, SettingsService>();
builder.Services.AddSingleton<IActuarialService, ActuarialService>();
builder.Services.AddSingleton<ISystemStatusService, SystemStatusService>();
builder.Services.AddSingleton<ITwilioService, TwilioService>();
builder.Services.AddSingleton<ITelegramService, TelegramService>();
builder.Services.AddSingleton<IChartService, ChartService>();
builder.Services.AddTransient<IEmailService, EmailService>();
builder.Services.AddScoped<IFinancialReportService, FinancialReportService>();
builder.Services.AddScoped<ITransactionSyncService, TransactionSyncService>();
builder.Services.AddScoped<ISubscriptionService, SubscriptionService>();
builder.Services.AddSingleton<ILogSinkService, LogSinkService>();
builder.Services.AddTransient<DatabaseInitializer>();

// Background Services
builder.Services.AddHostedService<TransactionsBackgroundService>();
builder.Services.AddHostedService<WeeklyReportWorker>();
builder.Services.AddHostedService<DailyBriefingWorker>();
builder.Services.AddHostedService<ConnectivityWorker>();

// ENHANCEMENT: Add TelegramChatService as hosted service and injectable service
builder.Services.AddSingleton<TelegramChatService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<TelegramChatService>());
builder.Services.AddSingleton<ITelegramChatService>(provider => provider.GetRequiredService<TelegramChatService>());

// ENHANCEMENT: Add health checks
builder.Services.AddHealthChecks()
    .AddNpgSql(
        builder.Configuration.GetConnectionString("DefaultConnection")!,
        name: "database",
        tags: new[] { "db", "sql", "timescale" })
    .AddCheck<AiHealthCheck>("ai_service", tags: new[] { "ai", "external" })
    .AddCheck<InvestecHealthCheck>("investec_api", tags: new[] { "investec", "external" });

var app = builder.Build();

// Ensure DB is ready
using (var scope = app.Services.CreateScope())
{
    var initializer = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
    await initializer.InitializeAsync();
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseForwardedHeaders();

// Global Request Logger
app.Use(async (context, next) =>
{
    var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("Request: {Method} {Path}", context.Request.Method, context.Request.Path);
    await next();
});

app.UseStaticFiles();
app.UseRouting();

// ENHANCEMENT: Enable rate limiting
app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

app.UseMiddleware<SecurityValidationMiddleware>();

app.MapControllers();

// ENHANCEMENT: Map health check endpoints
app.MapHealthChecks("/health");
app.MapHealthChecks("/health/ready");
app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("db")
});

app.MapFallbackToFile("index.html");

app.Run();

// ENHANCEMENT: Health Check implementations
namespace GordonWorker.Services
{
    public class AiHealthCheck : Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheck
    {
        private readonly IAiService _aiService;
        private readonly ILogger<AiHealthCheck> _logger;

        public AiHealthCheck(IAiService aiService, ILogger<AiHealthCheck> logger)
        {
            _aiService = aiService;
            _logger = logger;
        }

        public async Task<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult> CheckHealthAsync(
            Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext context,
            CancellationToken cancellationToken = default)
        {
            try
            {
                // Test with user 1 (admin) - this is a basic connectivity check
                var (success, error) = await _aiService.TestConnectionAsync(1);

                if (success)
                    return Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy("AI service is responsive");

                _logger.LogWarning("AI health check failed: {Error}", error);
                return Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Degraded($"AI service check failed: {error}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AI health check exception");
                return Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Unhealthy("AI service is unreachable", ex);
            }
        }
    }

    public class InvestecHealthCheck : Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheck
    {
        private readonly ILogger<InvestecHealthCheck> _logger;
        private readonly HttpClient _httpClient;

        public InvestecHealthCheck(ILogger<InvestecHealthCheck> logger, IHttpClientFactory httpClientFactory)
        {
            _logger = logger;
            _httpClient = httpClientFactory.CreateClient();
        }

        public async Task<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult> CheckHealthAsync(
            Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext context,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var response = await _httpClient.GetAsync("https://openapi.investec.com/", cancellationToken);

                if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    return Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy("Investec API is reachable");

                _logger.LogWarning("Investec API returned {StatusCode}", response.StatusCode);
                return Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Degraded($"Investec API returned {response.StatusCode}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Investec health check exception");
                return Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Unhealthy("Investec API is unreachable", ex);
            }
        }
    }
}
