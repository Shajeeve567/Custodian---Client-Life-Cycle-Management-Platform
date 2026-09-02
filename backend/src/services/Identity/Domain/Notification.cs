namespace Custodian.Identity.Domain;

public sealed class Notification
{
    public Guid NotificationId { get; set; }
    public Guid TenantId { get; set; }
    public Guid ClientId { get; set; }
    public string Message { get; set; } = string.Empty;
    public string SourceEventType { get; set; } = string.Empty;
    public bool IsRead { get; set; } = false;
    public DateTimeOffset CreatedAt { get; set; }

    public ClientProfile? Client { get; set; }
}
