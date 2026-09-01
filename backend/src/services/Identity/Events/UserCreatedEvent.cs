using Custodian.Shared.Auth;

namespace Custodian.Identity.Events;

public sealed record UserCreatedEvent(Guid UserId, Guid TenantId, string Email, Role Role);