namespace Custodian.Identity.Contracts;

public sealed record NotificationResponse(
    Guid NotificationId,
    Guid TenantId,
    Guid ClientId,
    string Message,
    string SourceEventType,
    bool IsRead,
    DateTimeOffset CreatedAt);

public sealed record CreateNotificationRequest(
    Guid ClientId,
    string Message,
    string SourceEventType);
