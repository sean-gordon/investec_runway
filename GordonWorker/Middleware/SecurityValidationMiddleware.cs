using GordonWorker.Services;
using Twilio.Security;
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

    public async Task InvokeAsync(HttpContext context, ISettingsService settingsService)
    {
        var path = context.Request.Path;
        var host = context.Request.Host.Host;

        // Rule 1: Allow if from *.wethegordons.co.za or localhost
        bool isGordonDomain = host.EndsWith(".wethegordons.co.za", StringComparison.OrdinalIgnoreCase) || 
                             host.Equals("wethegordons.co.za", StringComparison.OrdinalIgnoreCase) ||
                             host.Equals("localhost", StringComparison.OrdinalIgnoreCase);

        // If it's the WhatsApp webhook, we MUST validate the Twilio signature regardless of the domain
        // to ensure it's actually Twilio calling us.
        if (path.StartsWithSegments("/api/WhatsApp/webhook"))
        {
            var settings = await settingsService.GetSettingsAsync();
            var authToken = settings.TwilioAuthToken;
            var signature = context.Request.Headers["X-Twilio-Signature"].ToString();

            if (string.IsNullOrEmpty(authToken))
            {
                _logger.LogWarning("Twilio Auth Token not configured. Proceeding without signature validation (Insecure!).");
                await _next(context);
                return;
            }

            if (string.IsNullOrEmpty(signature))
            {
                _logger.LogWarning("Missing X-Twilio-Signature header for webhook request.");
                context.Response.StatusCode = 403;
                await context.Response.WriteAsync("Forbidden: Missing signature.");
                return;
            }

            // Twilio validation requires the absolute URL exactly as Twilio called it.
            var requestUrl = GetFullRequestUrl(context);
            
            context.Request.EnableBuffering();
            
            // For x-www-form-urlencoded, we read the form parameters
            var form = await context.Request.ReadFormAsync();
            var parameters = form.ToDictionary(k => k.Key, v => v.Value.ToString());

            var validator = new RequestValidator(authToken);
            if (validator.Validate(requestUrl, parameters, signature))
            {
                // Reset body position so the Controller can read it too
                context.Request.Body.Position = 0;
                await _next(context);
                return;
            }

            _logger.LogWarning("Twilio signature validation failed. URL: {Url}, Host: {Host}", requestUrl, host);
            context.Response.StatusCode = 403;
            await context.Response.WriteAsync("Forbidden: Invalid Twilio signature.");
            return;
        }

        // For all other requests, ensure they are on the Gordon domain
        if (isGordonDomain)
        {
            await _next(context);
            return;
        }

        _logger.LogWarning("Blocked request from unauthorized host: {Host} for path: {Path}", host, path);
        context.Response.StatusCode = 403;
        await context.Response.WriteAsync("Forbidden: Unauthorized domain.");
    }

    private string GetFullRequestUrl(HttpContext context)
    {
        // Handle proxies (Cloudflare/Nginx) which might change protocol or host
        var protocol = context.Request.Headers["X-Forwarded-Proto"].ToString();
        if (string.IsNullOrEmpty(protocol)) protocol = context.Request.Scheme;

        var host = context.Request.Headers["X-Forwarded-Host"].ToString();
        if (string.IsNullOrEmpty(host)) host = context.Request.Host.ToString();

        var path = context.Request.PathBase + context.Request.Path + context.Request.QueryString;

        return $"{protocol}://{host}{path}";
    }
}
