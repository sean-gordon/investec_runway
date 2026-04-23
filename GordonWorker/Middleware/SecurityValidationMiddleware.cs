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

        // Allow Telegram webhook with secret token
        if (path.StartsWithSegments("/telegram/webhook"))
        {
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
        // SECURITY FIX: Exact match, authorized subdomain, or explicit wildcard (*)
        bool isAllowedDomain = _allowedDomains.Any(domain =>
            domain == "*" ||
            effectiveHost.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
            effectiveHost.EndsWith($".{domain}", StringComparison.OrdinalIgnoreCase));

        if (!isAllowedDomain)
        {
            _logger.LogWarning("Blocked request from unauthorized host: {Host} for path: {Path}", effectiveHost, path);
            context.Response.StatusCode = 403;
            await context.Response.WriteAsync("Forbidden: Unauthorized domain.");
            return;
        }

        // SECURITY FIX: Basic CSRF mitigation for browser requests
        var origin = context.Request.Headers["Origin"].ToString();
        var referer = context.Request.Headers["Referer"].ToString();
        
        if (!string.IsNullOrEmpty(origin) && !IsAllowedOrigin(origin))
        {
            _logger.LogWarning("Blocked request due to invalid Origin: {Origin}", origin);
            context.Response.StatusCode = 403;
            return;
        }

        await _next(context);
    }

    private bool IsAllowedOrigin(string origin)
    {
        var uri = new Uri(origin);
        var host = uri.Host;
        return _allowedDomains.Any(d => d == "*" || host.Equals(d, StringComparison.OrdinalIgnoreCase) || host.EndsWith($".{d}", StringComparison.OrdinalIgnoreCase));
    }
}
