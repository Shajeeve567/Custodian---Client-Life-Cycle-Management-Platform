namespace Custodian.Identity.Contracts;

public sealed record NotificationResponse(
    Guid NotificationId,
    Guid TenantId,
    Guid ClientId,
    string Message,
    string SourceEventType,
    bool IsRead,
    DateTimeOffset CreatedAt);

public sealed record UnreadCountResponse(
    int UnreadCount);

public sealed record MarkAsReadResponse(
    Guid NotificationId,
    bool IsRead,
    DateTimeOffset UpdatedAtUtc);

public sealed record MarkAllAsReadResponse(
    int UpdatedCount,
    DateTimeOffset UpdatedAtUtc);

public sealed record CreateNotificationRequest(
    Guid ClientId,
    string Message,
    string SourceEventType);
