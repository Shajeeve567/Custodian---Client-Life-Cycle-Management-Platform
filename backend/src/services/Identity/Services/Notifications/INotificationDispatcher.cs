namespace Custodian.Identity.Services.Notifications;

public interface INotificationDispatcher
{
    Task DispatchAsync(NotificationContext context, CancellationToken ct = default);
}
