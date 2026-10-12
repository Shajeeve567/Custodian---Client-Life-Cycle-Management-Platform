using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Custodian.Audit.Tests.Integration;

/// <summary>
/// Mints JWTs for integration tests. Key/issuer/audience MUST match
/// Audit's appsettings.json or every authenticated request 401s.
/// </summary>
internal static class TestTokenFactory
{
    private const string Key = "custodian_super_secret_development_signing_key_at_least_64_bytes_long_1234567890";
    private const string Issuer = "custodian-identity";
    private const string Audience = "custodian-services";

    public static string CreateOwnerToken(string tenantId, string? email = null, string? userId = null) =>
        CreateToken("Owner", tenantId, userId, ResolveEmailOrUserId(email, ref userId, "owner@custodian.com"));

    public static string CreateStaffToken(string tenantId, string? email = null, string? userId = null) =>
        CreateToken("Staff", tenantId, userId, ResolveEmailOrUserId(email, ref userId, "staff@custodian.com"));

    public static string CreateClientToken(string tenantId, string? email = null, string? userId = null) =>
        CreateToken("Client", tenantId, userId, ResolveEmailOrUserId(email, ref userId, "client@custodian.com"));

    public static string CreateGlobalTokenWithoutTenant(string role = "Owner", string? userId = null, string? email = null) =>
        CreateToken(role, null, userId, email ?? $"{role.ToLowerInvariant()}@custodian.com");

    public static string CreateToken(string tenantId, string role, string email = "user@custodian.com") =>
        CreateToken(role, tenantId, null, email);

    public static string CreateToken(string role, string? tenantId = null, string? userId = null, string? email = null)
    {
        var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Key));
        var creds = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

        var sub = userId ?? Guid.NewGuid().ToString();
        var effectiveEmail = email ?? $"{role.ToLowerInvariant()}@custodian.com";

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, sub),
            new(ClaimTypes.NameIdentifier, sub),
            new(ClaimTypes.Role, role),
            new("role", role),
            new(ClaimTypes.Email, effectiveEmail),
            new("email", effectiveEmail),
            new(ClaimTypes.Name, effectiveEmail)
        };

        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            claims.Add(new Claim("tenant_id", tenantId));
            claims.Add(new Claim("tenantId", tenantId));
        }

        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static string ResolveEmailOrUserId(string? email, ref string? userId, string defaultEmail)
    {
        if (email == null)
        {
            return defaultEmail;
        }

        if (email.Contains('@'))
        {
            return email;
        }

        // Positional argument was passed without '@' (intended as a userId, not an email)
        if (userId == null)
        {
            userId = email;
        }

        return defaultEmail;
    }
}