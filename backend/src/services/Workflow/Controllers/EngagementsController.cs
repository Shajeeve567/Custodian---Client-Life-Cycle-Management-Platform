using Custodian.Workflow.DTOs;
using Custodian.Workflow.Models;
using Custodian.Workflow.Repositories;
using Custodian.Workflow.Services;
using Microsoft.AspNetCore.Mvc;

namespace Custodian.Workflow.Controllers;

[ApiController]
[Route("api/[controller]")]
public class EngagementsController : ControllerBase
{
    private readonly IEngagementRepository _repository;

    public EngagementsController(IEngagementRepository repository)
    {
        _repository = repository;
    }

    [HttpPost]
    public async Task<ActionResult<EngagementResponse>> CreateEngagement([FromBody] CreateEngagementRequest request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var effectiveTenantId = ResolveTenantId(request.TenantId);
        if (string.IsNullOrWhiteSpace(effectiveTenantId))
        {
            return BadRequest("Tenant identification is required.");
        }

        var engagement = new Engagement
        {
            EngagementId = Guid.NewGuid(),
            TenantId = effectiveTenantId,
            ClientId = request.ClientId,
            StaffId = request.StaffId,
            Status = EngagementStatus.Draft,
            CreatedAt = DateTime.UtcNow
        };

        var created = await _repository.CreateAsync(engagement);

        return CreatedAtAction(nameof(GetEngagementById), new { id = created.EngagementId, tenantId = created.TenantId }, MapToResponse(created));
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<EngagementResponse>> GetEngagementById(Guid id, [FromQuery] string? tenantId)
    {
        var effectiveTenantId = ResolveTenantId(tenantId);
        if (string.IsNullOrWhiteSpace(effectiveTenantId))
        {
            return BadRequest("tenantId parameter or JWT tenant claim is required for tenant isolation.");
        }

        var engagement = await _repository.GetByIdAsync(id, effectiveTenantId);

        if (engagement == null)
        {
            return NotFound();
        }

        return Ok(MapToResponse(engagement));
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<EngagementResponse>>> GetEngagements([FromQuery] string? tenantId)
    {
        var effectiveTenantId = ResolveTenantId(tenantId);
        if (string.IsNullOrWhiteSpace(effectiveTenantId))
        {
            return BadRequest("tenantId parameter or JWT tenant claim is required for tenant isolation.");
        }

        var engagements = await _repository.GetAllByTenantAsync(effectiveTenantId);

        return Ok(engagements.Select(MapToResponse));
    }

    [HttpPut("{id}/status")]
    public async Task<ActionResult<EngagementResponse>> UpdateStatus(Guid id, [FromBody] UpdateEngagementStatusRequest request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var effectiveTenantId = ResolveTenantId(request.TenantId);
        if (string.IsNullOrWhiteSpace(effectiveTenantId))
        {
            return BadRequest("Tenant identification is required.");
        }

        var engagement = await _repository.GetByIdAsync(id, effectiveTenantId);

        if (engagement == null)
        {
            return NotFound();
        }

        if (!Enum.TryParse<EngagementStatus>(request.Status, true, out var newStatus))
        {
            return BadRequest($"Invalid status: '{request.Status}'. Valid statuses are: Draft, Started, Closed, Cancelled.");
        }

        // Subtask 3 Lifecycle Validation: Enforce legal status transitions
        if (!EngagementLifecycleValidator.IsValidTransition(engagement.Status, newStatus))
        {
            return BadRequest(new
            {
                message = $"Invalid status transition from '{engagement.Status}' to '{newStatus}'."
            });
        }

        engagement.Status = newStatus;
        if (newStatus == EngagementStatus.Closed || newStatus == EngagementStatus.Cancelled)
        {
            engagement.ClosedAt = DateTime.UtcNow;
        }

        var updated = await _repository.UpdateAsync(engagement);
        return Ok(MapToResponse(updated));
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteEngagement(Guid id, [FromQuery] string? tenantId)
    {
        var effectiveTenantId = ResolveTenantId(tenantId);
        if (string.IsNullOrWhiteSpace(effectiveTenantId))
        {
            return BadRequest("tenantId parameter or JWT tenant claim is required for tenant isolation.");
        }

        var engagement = await _repository.GetByIdAsync(id, effectiveTenantId);

        if (engagement == null)
        {
            return NotFound();
        }

        // Subtask 3 Lifecycle Protection Rule: Started and Closed engagements CANNOT be physically deleted
        if (!EngagementLifecycleValidator.CanDelete(engagement.Status))
        {
            return Conflict(new
            {
                message = $"Engagement in state '{engagement.Status}' cannot be physically deleted per Custodian lifecycle protection rules."
            });
        }

        await _repository.DeleteAsync(id, effectiveTenantId);

        return NoContent();
    }

    /// <summary>
    /// Subtask 4: Resolves tenant ID server-side from HttpContext JWT claims if authenticated.
    /// Falls back to request parameter if claims are not populated.
    /// </summary>
    private string? ResolveTenantId(string? requestTenantId)
    {
        var jwtTenantId = User?.FindFirst("tenant_id")?.Value ?? User?.FindFirst("tenantId")?.Value;

        if (!string.IsNullOrWhiteSpace(jwtTenantId))
        {
            return jwtTenantId; // Authenticated JWT claim takes precedence
        }

        return requestTenantId;
    }

    private static EngagementResponse MapToResponse(Engagement e) => new()
    {
        EngagementId = e.EngagementId,
        TenantId = e.TenantId,
        ClientId = e.ClientId,
        StaffId = e.StaffId,
        Status = e.Status.ToString(),
        CreatedAt = e.CreatedAt,
        ClosedAt = e.ClosedAt
    };
}
