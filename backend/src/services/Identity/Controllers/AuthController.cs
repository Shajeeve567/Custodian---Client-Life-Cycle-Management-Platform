using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Custodian.Shared.Auth;
using Identity.Data;
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

        // 3. Create the Claims (the data inside the token)
        var claims = new List<Claim>
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, user.Email),
            new Claim("tenant_id", user.TenantId.ToString()), // <-- The TenantContext middleware looks for this exact claim!
            new Claim(ClaimTypes.Role, user.Role.ToString())
        };

        // 4. Generate the Signature Key
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.SigningKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        // 5. Build the Token
        var token = new JwtSecurityToken(
            issuer: jwtOptions.Issuer,
            audience: jwtOptions.Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(jwtOptions.ExpiryMinutes),
            signingCredentials: creds
        );

        var tokenString = new JwtSecurityTokenHandler().WriteToken(token);

        return Ok(new LoginResponse(tokenString, jwtOptions.ExpiryMinutes));
    }
}

public record LoginRequest(string Email, string Password);
public record LoginResponse(string Token, int ExpiresInMinutes);
