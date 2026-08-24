using Custodian.Identity.Domain;
using Custodian.Shared.Tenancy;
using Identity.Data;
using Microsoft.AspNetCore.Mvc;

namespace Identity.Controllers;

[Route("api/[controller]")]
[ApiController]
public class ClientController(IClientProfileRepository repo, TenantContext tenantContext) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<ClientProfile>>> GetClients(CancellationToken cancellationToken)
    {
        var tenantId = Guid.Parse(tenantContext.RequireTenantId());
        var clients = await repo.ListByTenantAsync(tenantId, cancellationToken);
        return Ok(clients);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ClientProfile>> GetClient(Guid id, CancellationToken cancellationToken)
    {
        var tenantId = Guid.Parse(tenantContext.RequireTenantId());
        var client = await repo.GetByIdAsync(id, cancellationToken);
        
        // Security check: Make sure this client actually belongs to the requesting tenant!
        if (client is null || client.TenantId != tenantId)
        {
            return NotFound();
        }

        return Ok(client);
    }

    [HttpPost]
    public async Task<ActionResult<ClientProfile>> CreateClient([FromBody] CreateClientRequest request, CancellationToken cancellationToken)
    {
        var tenantId = Guid.Parse(tenantContext.RequireTenantId());
        
        var client = new ClientProfile
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = request.Name,
            Email = request.Email,
            Phone = request.Phone,
            Status = UserStatus.Active, // Assuming Active is a valid enum value
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        await repo.AddAsync(client, cancellationToken);

        return CreatedAtAction(nameof(GetClient), new { id = client.Id }, client);
    }

    [HttpPatch("{id:guid}/deactivate")]
    public async Task<ActionResult> DeactivateClient(Guid id, CancellationToken cancellationToken)
    {
        var tenantId = Guid.Parse(tenantContext.RequireTenantId());
        var client = await repo.GetByIdAsync(id, cancellationToken);
        
        if (client is null || client.TenantId != tenantId)
        {
            return NotFound();
        }

        if (client.Status != UserStatus.Deactivated)
        {
            client.Status = UserStatus.Deactivated;
            client.DeactivatedAtUtc = DateTimeOffset.UtcNow;
            await repo.UpdateAsync(client, cancellationToken);
        }

        return NoContent();
    }
}

// Using the C# 9 Positional Record we just talked about!
public record CreateClientRequest(string Name, string Email, string? Phone);
