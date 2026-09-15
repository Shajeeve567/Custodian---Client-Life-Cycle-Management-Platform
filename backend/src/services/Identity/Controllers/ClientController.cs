using Custodian.Identity.Domain;
using Custodian.Shared.Auth;
using Custodian.Shared.Tenancy;
using Identity.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Identity.Controllers;

[Authorize(Roles = "Owner,Staff")]
[Route("api/[controller]")]
[ApiController]
public class ClientController(
    IClientProfileRepository repo,
    IUserAccountRepository userRepo,
    TenantContext tenantContext) : ControllerBase
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
        if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 6)
        {
            return BadRequest("Password is required and must be at least 6 characters long.");
        }

        if (string.IsNullOrWhiteSpace(request.Email))
        {
            return BadRequest("Email is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest("Client name is required.");
        }

        var tenantId = Guid.Parse(tenantContext.RequireTenantId());

        // 1. Check if user already exists
        var existingUser = await userRepo.GetByEmailAsync(request.Email.Trim(), cancellationToken);
        if (existingUser is not null)
        {
            // If user already belongs to this tenant, reject duplicate
            if (existingUser.Memberships.Any(m => m.TenantId == tenantId))
            {
                return Conflict("A user or client with this email is already registered in this workspace.");
            }

            // Attach user to this workspace with Client role
            existingUser.Memberships.Add(new TenantMembership
            {
                UserId = existingUser.Id,
                TenantId = tenantId,
                Role = Role.Client
            });
            await userRepo.UpdateAsync(existingUser, cancellationToken);

            var existingClient = await repo.GetByIdAsync(existingUser.Id, cancellationToken);
            if (existingClient is null)
            {
                var newClientProfile = new ClientProfile
                {
                    Id = existingUser.Id,
                    TenantId = tenantId,
                    Name = request.Name.Trim(),
                    Email = request.Email.Trim(),
                    Phone = request.Phone?.Trim(),
                    Status = UserStatus.Active,
                    CreatedAtUtc = DateTimeOffset.UtcNow
                };
                await repo.AddAsync(newClientProfile, cancellationToken);
                return CreatedAtAction(nameof(GetClient), new { id = newClientProfile.Id }, newClientProfile);
            }

            return Ok(existingClient);
        }

        // 2. New client: Generate unified ID for both ClientProfile and UserAccount
        var clientId = Guid.NewGuid();

        var client = new ClientProfile
        {
            Id = clientId,
            TenantId = tenantId,
            Name = request.Name.Trim(),
            Email = request.Email.Trim(),
            Phone = request.Phone?.Trim(),
            Status = UserStatus.Active, 
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        var user = new UserAccount
        {
            Id = clientId,
            Email = request.Email.Trim(),
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            Status = UserStatus.Active,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Memberships = new List<TenantMembership>
            {
                new TenantMembership
                {
                    UserId = clientId,
                    TenantId = tenantId,
                    Role = Role.Client
                }
            }
        };

        await userRepo.AddAsync(user, cancellationToken);
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

public record CreateClientRequest(string Name, string Email, string? Phone, string Password);
public record UpdateClientRequest(string Name, string Email, string? Phone);
