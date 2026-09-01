using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Custodian.Shared.Auth;
using Identity.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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

    // PREVIOUS REGISTER ENDPOINT (moved/updated to UserAccountController register endpoint):
    // [HttpPost("register")]
    // public async Task<ActionResult> Register([FromBody] RegisterRequest request, CancellationToken cancellationToken)
    // {
    //     var existingUser = await userRepo.GetByEmailAsync(request.Email, cancellationToken);
    //     if (existingUser is not null) return Conflict("Email already exists.");
    //     var user = new UserAccount
    //     {
    //         Id = Guid.NewGuid(),
    //         Email = request.Email,
    //         PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
    //         Status = UserStatus.Active,
    //         CreatedAtUtc = DateTimeOffset.UtcNow,
    //         Memberships = new List<TenantMembership>()
    //     };
    //     await userRepo.AddAsync(user, cancellationToken);
    //     return Ok("User registered successfully. You can now login to get a Global Token.");
    // }

    // PREVIOUS SELECT-WORKSPACE SIGNATURE:
    // [HttpPost("select-workspace/{tenantId:guid}")]
    // [Authorize]
    // public async Task<ActionResult<LoginResponse>> SelectWorkspace(Guid tenantId, CancellationToken cancellationToken)
    // {
    //     var userIdStr = User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
    //     if (!Guid.TryParse(userIdStr, out var userId)) return Unauthorized();
    //     var user = await userRepo.GetByIdGlobalAsync(userId, cancellationToken);
    //     if (user is null) return Unauthorized();
    //     var membership = user.Memberships.FirstOrDefault(m => m.TenantId == tenantId);
    //     if (membership is null) return Forbid("You do not have access to this workspace.");

    // Select Workspace / Issue Workspace JWT Token Endpoint:
    // Accepts tenantId (as string, e.g. 'tenant-alpha' or Guid string) in route parameter.
    [HttpPost("select-workspace/{tenantId}")]
    [Authorize] // Require the global token!
    public async Task<ActionResult<LoginResponse>> SelectWorkspace(string tenantId, CancellationToken cancellationToken)
    {
        // 1. Convert tenant string to Guid (with MD5 fallback for string tenant names like 'tenant-alpha')
        Guid tenantGuid;
        if (!Guid.TryParse(tenantId, out tenantGuid))
        {
            using var md5 = System.Security.Cryptography.MD5.Create();
            var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(tenantId));
            tenantGuid = new Guid(hash);
        }

        // 2. Extract User ID from JWT Claims (checks NameIdentifier, Sub, and 'sub' claims)
        var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier) 
                     ?? User.FindFirstValue(JwtRegisteredClaimNames.Sub) 
                     ?? User.FindFirstValue("sub");

        if (!Guid.TryParse(userIdStr, out var userId)) return Unauthorized("Invalid user ID in token claim.");

        var user = await userRepo.GetByIdAsync(userId, cancellationToken);
        if (user is null) return Unauthorized("User not found.");

        // 3. Verify membership in target workspace
        var membership = user.Memberships.FirstOrDefault(m => m.TenantId == tenantGuid);
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

// PREVIOUS REGISTER REQUEST RECORD:
// public record RegisterRequest(string Email, string Password);

