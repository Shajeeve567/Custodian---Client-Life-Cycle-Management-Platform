using Custodian.Shared.Auth;

namespace Custodian.Identity.Domain;

public sealed class UserAccount
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public UserStatus Status { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? DeactivatedAtUtc { get; set; }
    
    // A user can belong to multiple tenants
    public List<TenantMembership> Memberships { get; set; } = [];
}