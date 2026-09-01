namespace Custodian.Shared.Auth;

public sealed record JwtTokenOptions(string Issuer, string Audience, string SigningKey, int ExpiryMinutes);