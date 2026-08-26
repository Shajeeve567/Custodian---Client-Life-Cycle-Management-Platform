using Custodian.Shared.Auth;

namespace Custodian.Identity.Contracts;

public sealed record LoginResponse(string AccessToken, Guid UserId, Guid TenantId, Role Role, DateTimeOffset ExpiresAtUtc);