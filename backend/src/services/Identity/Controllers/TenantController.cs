using Custodian.Identity.Domain;
using Custodian.Shared.Tenancy;
using Identity.Data;
using Microsoft.AspNetCore.Mvc;
namespace Identity.Controllers;

[Route("api/[controller]")]
[ApiController]
public class TenantController(ITenantRepository repo, TenantContext tenantContext) : ControllerBase
{
    [HttpGet("current")]
    public async Task<ActionResult<Tenant>> GetCurrentTenant(CancellationToken cancellationToken)
    {
        // RequireTenantId ensures they passed the X-Tenant-ID header
        var tenantId = Guid.Parse(tenantContext.RequireTenantId());
        
        var tenant = await repo.GetByIdAsync(tenantId, cancellationToken);
        if (tenant is null) return NotFound();
        
        return Ok(tenant);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<Tenant>> GetTenant(Guid id, CancellationToken cancellationToken)
    {
        var tenant = await repo.GetByIdAsync(id, cancellationToken);
        if (tenant is null) return NotFound();
        
        return Ok(tenant);
    }

    [HttpPost]
    public async Task<ActionResult<Tenant>> CreateTenant([FromBody] CreateTenantRequest request, CancellationToken cancellationToken)
    {
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        await repo.AddAsync(tenant, cancellationToken);

        return CreatedAtAction(nameof(GetTenant), new { id = tenant.Id }, tenant);
    }
}

public record CreateTenantRequest(string Name);