using Custodian.Identity.Domain;
using Identity.Data;
using Microsoft.Extensions.Logging;

namespace Custodian.Identity.Services.Notifications.Strategies;

public sealed class InAppPortalNotificationStrategy(
    INotificationRepository repository,
    ILogger<InAppPortalNotificationStrategy> logger) : INotificationDeliveryStrategy
{
    public NotificationChannel Channel => NotificationChannel.InAppPortal;

    public Task<bool> CanHandleAsync(NotificationContext context)
    {
        return Task.FromResult(context.TenantId != Guid.Empty && context.ClientId != Guid.Empty);
    }

    public async Task<bool> DeliverAsync(NotificationContext context, CancellationToken ct = default)
    {
        try
        {
            var notification = new Notification
            {
                NotificationId = Guid.NewGuid(),
                TenantId = context.TenantId,
                ClientId = context.ClientId,
                Message = context.Message,
                SourceEventType = context.SourceEventType,
                IsRead = false,
                CreatedAt = DateTimeOffset.UtcNow
            };

            await repository.CreateAsync(notification, ct);
            logger.LogInformation("In-App portal notification {NotificationId} saved for client {ClientId}",
                notification.NotificationId, context.ClientId);

            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to persist In-App notification for client {ClientId}", context.ClientId);
            return false;
        }
    }
}
