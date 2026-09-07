using Custodian.Identity.Domain;
using Custodian.Shared.Tenancy;
using Identity.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Identity.Controllers;


[Authorize(Roles = "Owner")]
[Route("api/[controller]")]
[ApiController]
public class UserAccountController(IUserAccountRepository repo, TenantContext tenantContext) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<UserAccount>>> GetUserAccounts(CancellationToken cancellationToken)
    {
        var users = await repo.ListAsync(cancellationToken);
        return Ok(users);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<UserAccount>> GetUserAccount(Guid id, CancellationToken cancellationToken)
    {
        var user = await repo.GetByIdAsync(id, cancellationToken);
        if (user is null)
        {
            return NotFound();
        }

        return Ok(user);
    }

    [HttpPatch("{id:guid}/deactivate")]
    public async Task<ActionResult> DeactivateUserAccount(Guid id, CancellationToken cancellationToken)
    {
        var user = await repo.GetByIdAsync(id, cancellationToken);
        if (user is null)
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


    [HttpPost("invite")]
    public async Task<ActionResult<UserAccount>> InviteUserAccount([FromBody] CreateUserRequest request, CancellationToken cancellationToken)
    {
        var tenantId = Guid.Parse(tenantContext.RequireTenantId());
        
        // Check if user already exists
        var existingUser = await repo.GetByEmailAsync(request.Email, cancellationToken);
        if (existingUser is not null)
        {
            // If user is already a member of this workspace:
            if (existingUser.Memberships.Any(m => m.TenantId == tenantId))
            {
                return Conflict("This user is already a member of this workspace.");
            }

            // User exists globally (e.g. self-registered), attach them to this tenant!
            existingUser.Memberships.Add(new TenantMembership
            {
                UserId = existingUser.Id,
                TenantId = tenantId,
                Role = request.Role
            });
            await repo.UpdateAsync(existingUser, cancellationToken);

            return Ok(existingUser);
        }

        var user = new UserAccount
        {
            Id = Guid.NewGuid(),
            Email = request.Email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            Status = UserStatus.Active,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Memberships = new List<TenantMembership>
            {
                new TenantMembership
                {
                    TenantId = tenantId,
                    Role = request.Role
                }
            }
        };
        await repo.AddAsync(user, cancellationToken);

        return Ok(user);
    }

    [HttpPut("{id:guid}/role")]
    public async Task<ActionResult> UpdateUserRole(Guid id, [FromBody] UpdateUserRoleRequest request, CancellationToken cancellationToken)
    {
        var tenantId = Guid.Parse(tenantContext.RequireTenantId());
        var user = await repo.GetByIdAsync(id, cancellationToken);
        if (user is null)
        {
            return NotFound();
        }

        var membership = user.Memberships.FirstOrDefault(m => m.TenantId == tenantId);
        if (membership is null)
        {
            return NotFound();
        }

        membership.Role = request.Role;
        await repo.UpdateAsync(user, cancellationToken);

        return NoContent();
    }
}

public record CreateUserRequest(string Email, string Password, Custodian.Shared.Auth.Role Role);
public record UpdateUserRoleRequest(Custodian.Shared.Auth.Role Role);
