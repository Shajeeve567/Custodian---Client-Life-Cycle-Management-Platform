using Custodian.Identity.Domain;
using Custodian.Shared.Tenancy;
using Custodian.Shared.Auth;
using Identity.Data;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Identity.Controllers;


[Authorize(Roles = "Owner")]
[Route("api/[controller]")]
[ApiController]
// PREVIOUS CLASS DECLARATION:
// public class UserAccountController(IUserAccountRepository repo, TenantContext tenantContext) : ControllerBase
public class UserAccountController(IUserAccountRepository repo, ITenantRepository tenantRepo, TenantContext tenantContext) : ControllerBase
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


    // PREVIOUS INVITE ENDPOINT CODE:
    // [HttpPost("invite")]
    // public async Task<ActionResult<UserAccount>> InviteUserAccount([FromBody] CreateUserRequest request, CancellationToken cancellationToken)
    // {
    //     var tenantId = Guid.Parse(tenantContext.RequireTenantId());
    //     var existingUser = await repo.GetByEmailAsync(request.Email, cancellationToken);
    //     if (existingUser is not null)
    //     {
    //         return Conflict("A user with this email already exists.");
    //     }
    //     var user = new UserAccount
    //     {
    //         Id = Guid.NewGuid(),
    //         Email = request.Email,
    //         PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
    //         Status = UserStatus.Active,
    //         CreatedAtUtc = DateTimeOffset.UtcNow,
    //         Memberships = new List<TenantMembership>
    //         {
    //             new TenantMembership
    //             {
    //                 TenantId = tenantId,
    //                 Role = request.Role
    //             }
    //         }
    //     };
    //     await repo.AddAsync(user, cancellationToken);
    //     return Ok(CreatedAtAction(nameof(GetUserAccount), new { id = user.Id }, user));
    // }

    // Public Registration Endpoint:
    // Marked [AllowAnonymous] so unauthenticated users can sign up.
    [AllowAnonymous]
    [HttpPost("register")]
    public async Task<ActionResult<UserAccount>> CreateNewUserAccount([FromBody] CreateUserRequest request, CancellationToken cancellationToken)
    {
        // 1. Resolve Tenant ID from request header or default to 'tenant-alpha'.
        // If tenant string is not a standard GUID (e.g. 'tenant-alpha'), generate a deterministic GUID via MD5 hash.
        var rawTenantId = tenantContext.TenantId ?? "tenant-alpha";
        Guid tenantId;
        if (!Guid.TryParse(rawTenantId, out tenantId))
        {
            using var md5 = System.Security.Cryptography.MD5.Create();
            var hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(rawTenantId));
            tenantId = new Guid(hash);
        }

        // 2. Ensure Tenant Record Exists:
        // Automatically creates the Tenant record in the Tenants table if it does not already exist.
        var tenant = await tenantRepo.GetByIdAsync(tenantId, cancellationToken);
        if (tenant is null)
        {
            tenant = new Tenant
            {
                Id = tenantId,
                Name = rawTenantId,
                CreatedAtUtc = DateTimeOffset.UtcNow
            };
            await tenantRepo.AddAsync(tenant, cancellationToken);
        }
        
        // 3. Email Uniqueness Check:
        // Checks if a user account with the requested email already exists.
        var existingUser = await repo.GetByEmailAsync(request.Email, cancellationToken);
        if (existingUser is not null)
        {
            return Conflict("A user with this email already exists.");
        }

        // 4. Flexible String Role Mapping:
        // Maps incoming string role values (case-insensitive) to internal Role enum.
        Role roleEnum = request.Role?.ToLower() switch
        {
            "admin" or "owner" => Role.Owner,
            "client" => Role.Client,
            _ => Role.Staff
        };

        // 5. Create User Account & Tenant Membership:
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
                    Role = roleEnum
                }
            }
        };
        await repo.AddAsync(user, cancellationToken);

        return CreatedAtAction(nameof(GetUserAccount), new { id = user.Id }, user);
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

// PREVIOUS RECORD DEFINITIONS:
// public record CreateUserRequest(string Email, string Password, Custodian.Shared.Auth.Role Role);
// public record UpdateUserRoleRequest(Custodian.Shared.Auth.Role Role);

public record CreateUserRequest(string Email, string Password, string Role);
public record UpdateUserRoleRequest(Role Role);


