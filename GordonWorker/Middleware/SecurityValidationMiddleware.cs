using GordonWorker.Services;
using Microsoft.AspNetCore.Http.Extensions;

namespace GordonWorker.Middleware;

public class SecurityValidationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<SecurityValidationMiddleware> _logger;

    public SecurityValidationMiddleware(RequestDelegate next, ILogger<SecurityValidationMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        
        var effectiveHost = context.Request.Headers["X-Forwarded-Host"].ToString();
        if (string.IsNullOrEmpty(effectiveHost)) effectiveHost = context.Request.Host.Host;
        else effectiveHost = effectiveHost.Split(':')[0]; 

        bool isGordonDomain = effectiveHost.EndsWith(".wethegordons.co.za", StringComparison.OrdinalIgnoreCase) || 
                             effectiveHost.Equals("wethegordons.co.za", StringComparison.OrdinalIgnoreCase) ||
                             effectiveHost.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                             effectiveHost.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase);

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

        if (isGordonDomain)
        {
            await _next(context);
            return;
        }

        _logger.LogWarning("Blocked request from unauthorized host: {Host} for path: {Path}", effectiveHost, path);
        context.Response.StatusCode = 403;
        await context.Response.WriteAsync("Forbidden: Unauthorized domain.");
    }
}
