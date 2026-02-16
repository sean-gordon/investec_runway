using System.Net;
using System.Net.Mail;

namespace GordonWorker.Services;

public interface IEmailService
{
    Task SendEmailAsync(int userId, string subject, string body);
    Task<bool> SendTestEmailAsync(int userId);
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

    private async Task<(string Host, int Port, string User, string Pass, string To)> GetEmailConfigAsync(int userId)
    {
        var settings = await _settingsService.GetSettingsAsync(userId);

        var host = settings.SmtpHost;
        var port = settings.SmtpPort > 0 ? settings.SmtpPort : 587;
        var user = settings.SmtpUser;
        var pass = settings.SmtpPass;
        var to = settings.EmailTo;

        return (host, port, user, pass, to);
    }

    public async Task SendEmailAsync(int userId, string subject, string body)
    {
        var config = await GetEmailConfigAsync(userId);

        if (string.IsNullOrEmpty(config.Host) || string.IsNullOrEmpty(config.To))
        {
            _logger.LogWarning("SMTP configuration missing for user {UserId}. Email not sent.", userId);
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

        mailMessage.To.Clear();
        var recipients = config.To.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var recipient in recipients)
        {
            mailMessage.To.Add(recipient.Trim());
        }

        try
        {
            await client.SendMailAsync(mailMessage);
            _logger.LogInformation("Email sent to {ToAddress} for user {UserId}", config.To, userId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send email for user {UserId}", userId);
            throw; 
        }
    }

    public async Task<bool> SendTestEmailAsync(int userId)
    {
        try
        {
            await SendEmailAsync(userId, "Gordon Finance: Test Email", "<h1>It Works!</h1><p>This is a test email from your Gordon Finance Engine.</p>");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Test email failed for user {UserId}", userId);
            return false;
        }
    }
}
