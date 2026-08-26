using Custodian.Identity.Domain;

namespace Custodian.Identity.Contracts;

public sealed record ClientResponse(
    Guid Id,
    Guid TenantId,
    string Name,
    string Email,
    string? Phone,
    UserStatus Status,
    DateTimeOffset CreatedAtUtc);