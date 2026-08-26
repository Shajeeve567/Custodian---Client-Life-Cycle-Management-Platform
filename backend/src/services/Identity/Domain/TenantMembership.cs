using Custodian.Shared.Auth;

namespace Custodian.Identity.Domain;

public sealed class TenantMembership
{
    public Guid UserId { get; set; }
    public Guid TenantId { get; set; }
    public Role Role { get; set; }
    
    public UserAccount User { get; set; } = null!;
    public Tenant Tenant { get; set; } = null!;
}
