using Identity.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Resend;

namespace Custodian.Identity.Services.Notifications.Strategies;

public sealed class ResendEmailNotificationStrategy : INotificationDeliveryStrategy
{
    private readonly IResend _resend;
    private readonly ResendOptions _options;
    private readonly IClientProfileRepository _clientRepository;
    private readonly ILogger<ResendEmailNotificationStrategy> _logger;

    public ResendEmailNotificationStrategy(
        IResend resend,
        IOptions<ResendOptions> options,
        IClientProfileRepository clientRepository,
        ILogger<ResendEmailNotificationStrategy> logger)
    {
        _resend = resend;
        _options = options.Value;
        _clientRepository = clientRepository;
        _logger = logger;
    }

    public NotificationChannel Channel => NotificationChannel.Email;

    public async Task<bool> CanHandleAsync(NotificationContext context)
    {
        if (!string.IsNullOrWhiteSpace(context.ClientEmail))
        {
            return true;
        }

        if (context.ClientId != Guid.Empty)
        {
            var client = await _clientRepository.GetByIdAsync(context.ClientId);
            return !string.IsNullOrWhiteSpace(client?.Email);
        }

        return false;
    }

    public async Task<bool> DeliverAsync(NotificationContext context, CancellationToken ct = default)
    {
        var recipientEmail = context.ClientEmail;
        if (string.IsNullOrWhiteSpace(recipientEmail) && context.ClientId != Guid.Empty)
        {
            var client = await _clientRepository.GetByIdAsync(context.ClientId, ct);
            recipientEmail = client?.Email;
        }

        if (string.IsNullOrWhiteSpace(recipientEmail))
        {
            _logger.LogWarning("Cannot deliver email notification: No recipient email for client {ClientId}", context.ClientId);
            return false;
        }

        // If no API key configured (e.g. Local dev / testing environment), log safely without failing
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            _logger.LogInformation("[DEV MOCK] Resend email simulated for {RecipientEmail}: {Subject} - {Message}",
                recipientEmail, context.Subject, context.Message);
            return true;
        }

        try
        {
            var emailMessage = new EmailMessage
            {
                From = _options.FromEmail,
                To = { recipientEmail },
                Subject = context.Subject,
                HtmlBody = $"<p>{System.Net.WebUtility.HtmlEncode(context.Message)}</p>"
            };

            var response = await _resend.EmailSendAsync(emailMessage, ct);

            if (response.Success)
            {
                _logger.LogInformation("Successfully sent Resend email to {RecipientEmail} for event {EventType}",
                    recipientEmail, context.SourceEventType);
                return true;
            }

            _logger.LogWarning("Resend API failed to send email to {RecipientEmail} for event {EventType}",
                recipientEmail, context.SourceEventType);

            return false;
        }
        catch (Exception ex)
        {
            // Per resilience rule: email failure must not crash or rollback transactions
            _logger.LogError(ex, "Failed to send Resend email to {RecipientEmail}", recipientEmail);
            return false;
        }
    }
}
