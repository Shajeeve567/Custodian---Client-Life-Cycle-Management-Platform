using Custodian.Identity.Domain;
using Custodian.Shared.Tenancy;
using Identity.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
namespace Identity.Controllers;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

[Authorize] 
[Route("api/[controller]")]
[ApiController]
public class TenantController(ITenantRepository repo, TenantContext tenantContext) : ControllerBase
{
    [Authorize(Roles = "Owner")] 
    [HttpGet("current")]
    public async Task<ActionResult<Tenant>> GetCurrentTenant(CancellationToken cancellationToken)
    {
        var tenantId = Guid.Parse(tenantContext.RequireTenantId());
        
        var tenant = await repo.GetByIdAsync(tenantId, cancellationToken);
        if (tenant is null) return NotFound();
        
        return Ok(tenant);
    }

    [Authorize(Roles = "Owner")] 
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<Tenant>> GetTenant(Guid id, CancellationToken cancellationToken)
    {
        var tenant = await repo.GetByIdAsync(id, cancellationToken);
        if (tenant is null) return NotFound();
        
        return Ok(tenant);
    }

    [HttpGet("mine")]
    public async Task<ActionResult<List<TenantMembershipResponse>>> GetMyTenants(CancellationToken cancellationToken)
    {
        // Works with the global token (no tenant_id claim yet): identify the caller from 'sub'.
        var userIdStr = User.FindFirstValue(JwtRegisteredClaimNames.Sub)
                     ?? User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (!Guid.TryParse(userIdStr, out var userId))
            return Unauthorized("You must be logged in to list your workspaces.");

        var memberships = await repo.ListMembershipsByUserAsync(userId, cancellationToken);

        var response = memberships
            .Where(m => m.Tenant is not null)
            .Select(m => new TenantMembershipResponse(
                m.TenantId,
                m.Tenant!.Name,
                m.Role,
                m.Tenant!.CreatedAtUtc))
            .ToList();

        return Ok(response);
    }

    [HttpPost]
    public async Task<ActionResult<Tenant>> CreateTenant([FromBody] CreateTenantRequest request, CancellationToken cancellationToken)
    {
        // 1. Find out who is making this request (from their Global Token)
        var userIdStr = User.FindFirstValue(JwtRegisteredClaimNames.Sub) 
                     ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        
        if (!Guid.TryParse(userIdStr, out var userId)) 
            return Unauthorized("You must be logged in to create a company.");

        // 2. Create the Tenant
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Memberships = new List<TenantMembership>()
        };

        // 3. Make the creator the Owner!
        tenant.Memberships.Add(new TenantMembership
        {
            UserId = userId,
            TenantId = tenant.Id,
            Role = Custodian.Shared.Auth.Role.Owner
        });

        // 4. Save both the Tenant and the Membership to the database simultaneously
        await repo.AddAsync(tenant, cancellationToken);

        return CreatedAtAction(nameof(GetTenant), new { id = tenant.Id }, tenant);
    }
}

public record CreateTenantRequest(string Name);

public record TenantMembershipResponse(
    Guid TenantId,
    string Name,
    Custodian.Shared.Auth.Role Role,
    DateTimeOffset CreatedAtUtc);