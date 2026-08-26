namespace Custodian.Identity.Contracts;

public sealed record ClientRequest(string Name, string Email, string? Phone);