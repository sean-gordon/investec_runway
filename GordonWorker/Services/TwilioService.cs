using GordonWorker.Services;
using Twilio;
using Twilio.Rest.Api.V2010.Account;
using Twilio.Types;

namespace GordonWorker.Services;

public interface ITwilioService
{
    Task SendWhatsAppMessageAsync(string to, string body);
}

public class TwilioService : ITwilioService
{
    private readonly ISettingsService _settingsService;
    private readonly ILogger<TwilioService> _logger;

    public TwilioService(ISettingsService settingsService, ILogger<TwilioService> logger)
    {
        _settingsService = settingsService;
        _logger = logger;
    }

    public async Task SendWhatsAppMessageAsync(string to, string body)
    {
        var settings = await _settingsService.GetSettingsAsync();

        if (string.IsNullOrWhiteSpace(settings.TwilioAccountSid) || 
            string.IsNullOrWhiteSpace(settings.TwilioAuthToken) || 
            string.IsNullOrWhiteSpace(settings.TwilioWhatsAppNumber))
        {
            _logger.LogWarning("Twilio settings are not fully configured. Cannot send WhatsApp message.");
            return;
        }

        try
        {
            TwilioClient.Init(settings.TwilioAccountSid, settings.TwilioAuthToken);

            var messageOptions = new CreateMessageOptions(new PhoneNumber(to));
            messageOptions.From = new PhoneNumber(settings.TwilioWhatsAppNumber);
            messageOptions.Body = body;

            var message = await MessageResource.CreateAsync(messageOptions);
            _logger.LogInformation("WhatsApp message sent to {To}. SID: {Sid}", to, message.Sid);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send WhatsApp message to {To}", to);
        }
    }
}
