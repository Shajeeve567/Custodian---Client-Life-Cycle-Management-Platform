using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Custodian.Workflow.Tests.Integration;

/// <summary>
/// Mints Owner-scoped JWTs for integration tests. Key/issuer/audience MUST match the
/// Workflow service's appsettings (Jwt section) or every authenticated request 401s.
/// Copy those values verbatim from appsettings.json — do not retype.
/// </summary>
internal static class TestTokenFactory
{
    private const string Key = "custodian_super_secret_development_signing_key_at_least_64_bytes_long_1234567890";
    private const string Issuer = "custodian-identity";
    private const string Audience = "custodian-services";

    public static string CreateOwnerToken(Guid tenantId)
    {
        var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Key));
        var creds = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString()),
                new Claim("tenant_id", tenantId.ToString()),
                new Claim(ClaimTypes.Role, "Owner"),
            },
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}