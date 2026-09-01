namespace Custodian.Identity.Events;

public sealed record TenantCreatedEvent(Guid TenantId, string Name);