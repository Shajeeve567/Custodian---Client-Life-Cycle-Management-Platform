namespace Custodian.Identity.Services.Notifications;

public interface INotificationDeliveryStrategy
{
    NotificationChannel Channel { get; }
    Task<bool> CanHandleAsync(NotificationContext context);
    Task<bool> DeliverAsync(NotificationContext context, CancellationToken ct = default);
}
