using Microsoft.AspNetCore.Http.Extensions;

namespace GordonWorker.Middleware;

public class SecurityValidationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<SecurityValidationMiddleware> _logger;
    private readonly List<string> _allowedDomains;

    public SecurityValidationMiddleware(RequestDelegate next, ILogger<SecurityValidationMiddleware> logger, IConfiguration configuration)
    {
        _next = next;
        _logger = logger;

        // SECURITY FIX: Load allowed domains from configuration
        var domainsConfig = configuration.GetSection("Security:AllowedDomains").Get<string[]>();
        _allowedDomains = domainsConfig?.ToList() ?? new List<string> { "localhost", "127.0.0.1" };

        _logger.LogInformation("Security middleware initialized with allowed domains: {Domains}", string.Join(", ", _allowedDomains));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;

        var effectiveHost = context.Request.Headers["X-Forwarded-Host"].ToString();
        if (string.IsNullOrEmpty(effectiveHost)) effectiveHost = context.Request.Host.Host;
        else effectiveHost = effectiveHost.Split(':')[0];

        // Allow Telegram webhook without domain check (it comes from Telegram servers)
        if (path.StartsWithSegments("/telegram/webhook"))
        {
            _logger.LogInformation("Telegram webhook hit detected. Bypassing domain check.");
            await _next(context);
            return;
        }

        // Allow WhatsApp webhook (Twilio servers) - validation will happen in Controller
        if (path.StartsWithSegments("/api/WhatsApp/webhook"))
        {
            await _next(context);
            return;
        }

        // Allow health check endpoints
        if (path.StartsWithSegments("/health"))
        {
            await _next(context);
            return;
        }

        // Check if host is in allowed domains list
        bool isAllowedDomain = _allowedDomains.Any(domain =>
            effectiveHost.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
            effectiveHost.EndsWith($".{domain}", StringComparison.OrdinalIgnoreCase));

        if (isAllowedDomain)
        {
            await _next(context);
            return;
        }

        _logger.LogWarning("Blocked request from unauthorized host: {Host} for path: {Path}", effectiveHost, path);
        context.Response.StatusCode = 403;
        await context.Response.WriteAsync("Forbidden: Unauthorized domain.");
    }
}
