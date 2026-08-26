using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Custodian.Shared.Auth;
using Identity.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace Identity.Controllers;

[Route("api/[controller]")]
[ApiController]
public class AuthController(IUserAccountRepository userRepo, IConfiguration configuration) : ControllerBase
{
    [HttpPost("login")]
    public async Task<ActionResult<LoginResponse>> Login([FromBody] LoginRequest request, CancellationToken cancellationToken)
    {
        // 1. Validate the User
        var user = await userRepo.GetByEmailAsync(request.Email, cancellationToken);
        
        // TODO: In a production app, you MUST use a password hasher (e.g., BCrypt.Verify) here!
        if (user is null || !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
        {
            return Unauthorized("Invalid email or password.");
        }

        // 2. Fetch the JWT configuration we set up earlier
        var jwtOptions = configuration.GetSection("Jwt").Get<JwtTokenOptions>();
        if (jwtOptions is null) return StatusCode(500, "JWT configuration is missing!");

        // 3. Create the Global Claims (No tenant_id yet!)
        var claims = new List<Claim>
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, user.Email)
        };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.SigningKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: jwtOptions.Issuer,
            audience: jwtOptions.Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(jwtOptions.ExpiryMinutes),
            signingCredentials: creds
        );

        return Ok(new LoginResponse(new JwtSecurityTokenHandler().WriteToken(token), jwtOptions.ExpiryMinutes));
    }

    [HttpPost("select-workspace/{tenantId:guid}")]
    [Authorize] // Require the global token!
    public async Task<ActionResult<LoginResponse>> SelectWorkspace(Guid tenantId, CancellationToken cancellationToken)
    {
        var userIdStr = User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdStr, out var userId)) return Unauthorized();

        var user = await userRepo.GetByIdAsync(userId, cancellationToken);
        if (user is null) return Unauthorized();

        // Verify they actually belong to this tenant
        var membership = user.Memberships.FirstOrDefault(m => m.TenantId == tenantId);
        if (membership is null) return Forbid("You do not have access to this workspace.");

        var jwtOptions = configuration.GetSection("Jwt").Get<JwtTokenOptions>();
        
        // Issue the new Workspace Token
        var claims = new List<Claim>
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, user.Email),
            new Claim("tenant_id", membership.TenantId.ToString()),
            new Claim(ClaimTypes.Role, membership.Role.ToString())
        };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions!.SigningKey));
        var token = new JwtSecurityToken(
            issuer: jwtOptions.Issuer,
            audience: jwtOptions.Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(jwtOptions.ExpiryMinutes),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256)
        );

        return Ok(new LoginResponse(new JwtSecurityTokenHandler().WriteToken(token), jwtOptions.ExpiryMinutes));
    }
}

public record LoginRequest(string Email, string Password);
public record LoginResponse(string Token, int ExpiresInMinutes);
