using System.Net;
using System.Net.Mail;

namespace GordonWorker.Services;

public interface IEmailService
{
    Task SendEmailAsync(string subject, string body);
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
            // In a real scenario, we might want to throw or handle this gracefully.
            // For now, logging the intent.
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
        }
    }
}
