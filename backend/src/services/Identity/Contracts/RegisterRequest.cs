using Custodian.Shared.Auth;

namespace Custodian.Identity.Contracts;

public sealed record RegisterRequest(string Email, string Password, string TenantName, Role Role);