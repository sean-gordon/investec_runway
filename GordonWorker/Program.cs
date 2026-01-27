using GordonWorker.Services;
using GordonWorker.Workers;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddHttpClient<IInvestecClient, InvestecClient>();
builder.Services.AddHttpClient<IOllamaService, OllamaService>();
builder.Services.AddSingleton<ISettingsService, SettingsService>();
builder.Services.AddSingleton<IActuarialService, ActuarialService>();
builder.Services.AddSingleton<ISystemStatusService, SystemStatusService>();
builder.Services.AddTransient<IEmailService, EmailService>();
builder.Services.AddScoped<IFinancialReportService, FinancialReportService>();
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

app.UseHttpsRedirection();
app.UseStaticFiles(); // Enable frontend
app.UseAuthorization();

app.MapControllers();
// Fallback to index.html for SPA
app.MapFallbackToFile("index.html");

app.Run();
