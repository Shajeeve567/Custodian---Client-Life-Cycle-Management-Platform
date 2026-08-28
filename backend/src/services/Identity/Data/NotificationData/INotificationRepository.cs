using Custodian.Identity.Domain;

namespace Identity.Data;

public interface INotificationRepository
{
    Task<Notification> CreateAsync(Notification notification, CancellationToken ct = default);
    Task<Notification?> GetByIdAsync(Guid notificationId, Guid tenantId, CancellationToken ct = default);
    Task<IEnumerable<Notification>> GetByClientAsync(Guid clientId, Guid tenantId, bool? unreadOnly = null, CancellationToken ct = default);
    Task<bool> MarkAsReadAsync(Guid notificationId, Guid tenantId, Guid clientId, CancellationToken ct = default);
    Task<int> MarkAllAsReadAsync(Guid clientId, Guid tenantId, CancellationToken ct = default);
}
