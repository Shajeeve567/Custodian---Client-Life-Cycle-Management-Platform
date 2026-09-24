using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Custodian.Audit.Tests.Integration;

/// <summary>
/// Mints Owner JWTs for integration tests. Key/issuer/audience MUST match
/// Audit's appsettings.json or every authenticated request 401s.
/// </summary>
internal static class TestTokenFactory
{
    private const string Key = "custodian_super_secret_development_signing_key_at_least_64_bytes_long_1234567890";
    private const string Issuer = "custodian-identity";
    private const string Audience = "custodian-services";

    public static string CreateOwnerToken(string tenantId)
    {
        var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Key));
        var creds = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString()),
                new Claim("tenant_id", tenantId),
                new Claim(ClaimTypes.Role, "Owner"),
            },
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}