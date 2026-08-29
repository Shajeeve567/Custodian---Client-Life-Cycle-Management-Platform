namespace Custodian.Identity.Services.Notifications.Mappers;

public sealed record ClientSafeMessageResult
{
    public string Subject { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public Guid ClientId { get; init; }
    public string? ClientEmail { get; init; }
}
