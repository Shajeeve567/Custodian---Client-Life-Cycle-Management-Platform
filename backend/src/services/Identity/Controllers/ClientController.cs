using Custodian.Identity.Domain;
using Custodian.Shared.Tenancy;
using Identity.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;


namespace Identity.Controllers;

[Authorize(Roles = "Owner,Staff")]
[Route("api/[controller]")]
[ApiController]
public class ClientController(IClientProfileRepository repo, TenantContext tenantContext) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<ClientProfile>>> GetClients(CancellationToken cancellationToken)
    {
        var clients = await repo.ListAsync(cancellationToken);
        return Ok(clients);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ClientProfile>> GetClient(Guid id, CancellationToken cancellationToken)
    {
        var client = await repo.GetByIdAsync(id, cancellationToken);
        
        if (client is null)
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
            Status = UserStatus.Active, 
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        await repo.AddAsync(client, cancellationToken);

        return CreatedAtAction(nameof(GetClient), new { id = client.Id }, client);
    }

    [HttpPatch("{id:guid}/deactivate")]
    public async Task<ActionResult> DeactivateClient(Guid id, CancellationToken cancellationToken)
    {
        var client = await repo.GetByIdAsync(id, cancellationToken);
        
        if (client is null)
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
    [HttpPut("{id:guid}")]
    public async Task<ActionResult<ClientProfile>> UpdateClient(Guid id, [FromBody] UpdateClientRequest request, CancellationToken cancellationToken)
    {
        var client = await repo.GetByIdAsync(id, cancellationToken);
        if (client is null)
        {
            return NotFound();
        }

        client.Name = request.Name;
        client.Email = request.Email;
        client.Phone = request.Phone;

        await repo.UpdateAsync(client, cancellationToken);
        return Ok(client);
    }
}

public record CreateClientRequest(string Name, string Email, string? Phone);
public record UpdateClientRequest(string Name, string Email, string? Phone);
