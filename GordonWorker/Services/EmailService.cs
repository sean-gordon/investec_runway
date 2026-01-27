using System.Net;
using System.Net.Mail;

namespace GordonWorker.Services;

public interface IEmailService
{
    Task SendEmailAsync(string subject, string body);
    Task<bool> SendTestEmailAsync();
}

public class EmailService : IEmailService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<EmailService> _logger;
    private readonly ISettingsService _settingsService;

    public EmailService(IConfiguration configuration, ILogger<EmailService> logger, ISettingsService settingsService)
    {
        _configuration = configuration;
        _logger = logger;
        _settingsService = settingsService;
    }

    private async Task<(string Host, int Port, string User, string Pass, string To)> GetEmailConfigAsync()
    {
        var settings = await _settingsService.GetSettingsAsync();

        var host = !string.IsNullOrWhiteSpace(settings.SmtpHost) ? settings.SmtpHost : _configuration["SMTP_HOST"];
        var port = settings.SmtpPort > 0 ? settings.SmtpPort : int.Parse(_configuration["SMTP_PORT"] ?? "587");
        var user = !string.IsNullOrWhiteSpace(settings.SmtpUser) ? settings.SmtpUser : _configuration["SMTP_USER"];
        var pass = !string.IsNullOrWhiteSpace(settings.SmtpPass) ? settings.SmtpPass : _configuration["SMTP_PASS"];
        var to = !string.IsNullOrWhiteSpace(settings.EmailTo) ? settings.EmailTo : _configuration["EMAIL_TO"];

        return (host ?? "", port, user ?? "", pass ?? "", to ?? "");
    }

    public async Task SendEmailAsync(string subject, string body)
    {
        var config = await GetEmailConfigAsync();

        if (string.IsNullOrEmpty(config.Host) || string.IsNullOrEmpty(config.To))
        {
            _logger.LogWarning("SMTP configuration missing. Email not sent.\nSubject: {Subject}", subject);
            return;
        }

        using var client = new SmtpClient(config.Host, config.Port)
        {
            Credentials = new NetworkCredential(config.User, config.Pass),
            EnableSsl = true
        };

        var mailMessage = new MailMessage
        {
            From = new MailAddress(config.User ?? "gordon@finance-engine.local", "Gordon Finance"),
            Subject = subject,
            Body = body,
            IsBodyHtml = true
        };

        mailMessage.To.Add(config.To);

        try
        {
            await client.SendMailAsync(mailMessage);
            _logger.LogInformation("Weekly report email sent to {ToAddress}", config.To);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send email.");
            throw; // Re-throw for visibility in test method
        }
    }

    public async Task<bool> SendTestEmailAsync()
    {
        try
        {
            await SendEmailAsync("Gordon Finance: Test Email", "<h1>It Works!</h1><p>This is a test email from your Gordon Finance Engine.</p>");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Test email failed.");
            return false;
        }
    }
}