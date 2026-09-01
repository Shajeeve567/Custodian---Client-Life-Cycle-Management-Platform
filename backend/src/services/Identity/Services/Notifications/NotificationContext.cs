namespace Custodian.Identity.Services.Notifications;

public sealed record NotificationContext
{
    public string EventId { get; init; } = string.Empty;
    public Guid TenantId { get; init; }
    public Guid ClientId { get; init; }
    public string? ClientEmail { get; init; }
    public string Subject { get; init; } = "Notification from Custodian";
    public string Message { get; init; } = string.Empty;
    public string SourceEventType { get; init; } = string.Empty;
    public IReadOnlyList<NotificationChannel> Channels { get; init; } = new[] 
    { 
        NotificationChannel.InAppPortal, 
        NotificationChannel.Email 
    };
}
