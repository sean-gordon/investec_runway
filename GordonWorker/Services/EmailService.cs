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

    public EmailService(IConfiguration configuration, ILogger<EmailService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task SendEmailAsync(string subject, string body)
    {
        var smtpHost = _configuration["SMTP_HOST"];
        var smtpPort = int.Parse(_configuration["SMTP_PORT"] ?? "587");
        var smtpUser = _configuration["SMTP_USER"];
        var smtpPass = _configuration["SMTP_PASS"];
        var toAddress = _configuration["EMAIL_TO"];

        if (string.IsNullOrEmpty(smtpHost) || string.IsNullOrEmpty(toAddress))
        {
            _logger.LogWarning("SMTP configuration missing. Email not sent.\nSubject: {Subject}", subject);
            return;
        }

        using var client = new SmtpClient(smtpHost, smtpPort)
        {
            Credentials = new NetworkCredential(smtpUser, smtpPass),
            EnableSsl = true
        };

        var mailMessage = new MailMessage
        {
            From = new MailAddress(smtpUser ?? "gordon@finance-engine.local", "Gordon Finance"),
            Subject = subject,
            Body = body,
            IsBodyHtml = true
        };

        mailMessage.To.Add(toAddress);

        try
        {
            await client.SendMailAsync(mailMessage);
            _logger.LogInformation("Weekly report email sent to {ToAddress}", toAddress);
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
