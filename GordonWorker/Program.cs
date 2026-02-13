using GordonWorker.Services;
using GordonWorker.Workers;
using GordonWorker.Middleware;
using Microsoft.AspNetCore.HttpOverrides;
using Dapper;
using Microsoft.AspNetCore.DataProtection;

// Support legacy timestamp behavior for Npgsql (Postgres)
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
AppContext.SetSwitch("Npgsql.DisableDateTimeInfinityConversions", true);

var builder = WebApplication.CreateBuilder(args);

// Enable snake_case mapping for Dapper (Postgres style)
DefaultTypeMap.MatchNamesWithUnderscores = true;

// Add services to the container.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo("keys"));

builder.Services.AddHttpClient<IInvestecClient, InvestecClient>();
builder.Services.AddHttpClient<IAiService, AiService>();
builder.Services.AddSingleton<ISettingsService, SettingsService>();
builder.Services.AddSingleton<IActuarialService, ActuarialService>();
builder.Services.AddSingleton<ISystemStatusService, SystemStatusService>();
builder.Services.AddSingleton<ITwilioService, TwilioService>();
builder.Services.AddSingleton<ITelegramService, TelegramService>();
builder.Services.AddTransient<IEmailService, EmailService>();
builder.Services.AddScoped<IFinancialReportService, FinancialReportService>();
builder.Services.AddScoped<ITransactionSyncService, TransactionSyncService>();
builder.Services.AddTransient<DatabaseInitializer>();

builder.Services.AddHostedService<TransactionsBackgroundService>();
builder.Services.AddHostedService<WeeklyReportWorker>();
builder.Services.AddHostedService<ConnectivityWorker>();

var app = builder.Build();

// Ensure DB is ready
using (var scope = app.Services.CreateScope())
{
    var initializer = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
    await initializer.InitializeAsync();
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseForwardedHeaders();
app.UseMiddleware<SecurityValidationMiddleware>();
app.UseStaticFiles(); // Enable frontend
app.UseAuthorization();

app.MapControllers();
// Fallback to index.html for SPA
app.MapFallbackToFile("index.html");

app.Run();
