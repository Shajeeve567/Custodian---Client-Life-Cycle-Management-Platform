using Custodian.Identity.Domain;
using Custodian.Shared.Tenancy;
using Identity.Data;
using Microsoft.AspNetCore.Mvc;
namespace Identity.Controllers;



[Route("api/[controller]")]
[ApiController]
public class UserAccountController(IUserAccountRepository repo, ITenantRepository tenantRepo, TenantContext tenantContext) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<UserAccount>>> GetUserAccounts(CancellationToken cancellationToken)
    {
        var tenantId = Guid.Parse(tenantContext.RequireTenantId());
        var users = await repo.ListByTenantAsync(tenantId, cancellationToken);
        return Ok(users);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<UserAccount>> GetUserAccount(Guid id, CancellationToken cancellationToken)
    {
        var tenantId = Guid.Parse(tenantContext.RequireTenantId());
        var user = await repo.GetByIdAsync(id, cancellationToken);
        if (user is null || user.TenantId != tenantId)
        {
            return NotFound();
        }

        return Ok(user);
    }

    [HttpPatch("{id:guid}/deactivate")]
    public async Task<ActionResult> DeactivateUserAccount(Guid id, CancellationToken cancellationToken)
    {
        var tenantId = Guid.Parse(tenantContext.RequireTenantId());
        var user = await repo.GetByIdAsync(id, cancellationToken);
        if (user is null || user.TenantId != tenantId)
        {
            return NotFound();
        }

        if (user.Status != UserStatus.Deactivated)
        {
            user.Status = UserStatus.Deactivated;
            user.DeactivatedAtUtc = DateTimeOffset.UtcNow;
            await repo.UpdateAsync(user, cancellationToken);
        }

        return NoContent();
    }


    [HttpPost]
    public async Task<ActionResult<UserAccount>> CreateNewUserAccount([FromBody] CreateUserRequest request, CancellationToken cancellationToken)
    {
        var tenantId = Guid.Parse(tenantContext.RequireTenantId());
        
        // Check if email is already taken
        var existingUser = await repo.GetByEmailAsync(request.Email, cancellationToken);
        if (existingUser is not null)
        {
            return Conflict("A user with this email already exists.");
        }

        var user = new UserAccount
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId, 
            Email = request.Email,
            // In a real app, you MUST hash the password here (e.g. using BCrypt or ASP.NET Identity)
            PasswordHash = request.Password, 
            Role = request.Role,
            Status = UserStatus.Active,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        // Saving the UserAccount with the TenantId automatically links it 
        // to the Tenant in the database via the foreign key!
        await repo.AddAsync(user, cancellationToken);

        return CreatedAtAction(nameof(GetUserAccount), new { id = user.Id }, user);
    }
}

public record CreateUserRequest(string Email, string Password, Custodian.Shared.Auth.Role Role);
