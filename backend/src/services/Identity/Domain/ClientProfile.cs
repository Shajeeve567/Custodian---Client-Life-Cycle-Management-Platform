namespace Custodian.Identity.Domain;

public sealed class ClientProfile
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public UserStatus Status { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? DeactivatedAtUtc { get; set; }
    public Tenant? Tenant { get; set; }
}