using Custodian.Identity.Domain;
using Microsoft.EntityFrameworkCore;

namespace Identity.Data;

public sealed class NotificationRepository(IdentityDbContext db) : INotificationRepository
{
    public async Task<Notification> CreateAsync(Notification notification, CancellationToken ct = default)
    {
        db.Notifications.Add(notification);
        await db.SaveChangesAsync(ct);
        return notification;
    }

    public Task<Notification?> GetByIdAsync(Guid notificationId, Guid tenantId, CancellationToken ct = default) =>
        db.Notifications
            .FirstOrDefaultAsync(n => n.NotificationId == notificationId && n.TenantId == tenantId, ct);

    public async Task<IEnumerable<Notification>> GetByClientAsync(
        Guid clientId, 
        Guid tenantId, 
        bool? unreadOnly = null, 
        CancellationToken ct = default)
    {
        var query = db.Notifications
            .Where(n => n.TenantId == tenantId && n.ClientId == clientId);

        if (unreadOnly.HasValue && unreadOnly.Value)
        {
            query = query.Where(n => !n.IsRead);
        }

        return await query
            .OrderByDescending(n => n.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<bool> MarkAsReadAsync(
        Guid notificationId, 
        Guid tenantId, 
        Guid clientId, 
        CancellationToken ct = default)
    {
        var notification = await db.Notifications
            .FirstOrDefaultAsync(n => n.NotificationId == notificationId && n.TenantId == tenantId && n.ClientId == clientId, ct);

        if (notification == null)
        {
            return false;
        }

        if (!notification.IsRead)
        {
            notification.IsRead = true;
            await db.SaveChangesAsync(ct);
        }

        return true;
    }

    public async Task<int> MarkAllAsReadAsync(
        Guid clientId, 
        Guid tenantId, 
        CancellationToken ct = default)
    {
        var unreadNotifications = await db.Notifications
            .Where(n => n.TenantId == tenantId && n.ClientId == clientId && !n.IsRead)
            .ToListAsync(ct);

        if (unreadNotifications.Count == 0)
        {
            return 0;
        }

        foreach (var notification in unreadNotifications)
        {
            notification.IsRead = true;
        }

        return await db.SaveChangesAsync(ct);
    }
}
