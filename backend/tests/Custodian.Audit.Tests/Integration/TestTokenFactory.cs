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

    public static string CreateToken(string role, string? tenantId = null, string? userId = null, string? email = null)
    {
        var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Key));
        var creds = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId ?? Guid.NewGuid().ToString()),
            new(ClaimTypes.Role, role),
            new("role", role),
            new(ClaimTypes.Email, email ?? $"{role.ToLowerInvariant()}@custodian.test")
        };

        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            claims.Add(new Claim("tenant_id", tenantId));
        }

        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public static string CreateOwnerToken(string tenantId, string? userId = null) =>
        CreateToken("Owner", tenantId, userId);

    public static string CreateStaffToken(string tenantId, string? userId = null) =>
        CreateToken("Staff", tenantId, userId);

    public static string CreateClientToken(string tenantId, string? userId = null) =>
        CreateToken("Client", tenantId, userId);

    public static string CreateGlobalTokenWithoutTenant(string role = "Owner", string? userId = null) =>
        CreateToken(role, null, userId);
}