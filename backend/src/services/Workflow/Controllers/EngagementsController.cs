using Custodian.Workflow.DTOs;
using Custodian.Workflow.Models;
using Custodian.Workflow.Repositories;
using Custodian.Workflow.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Custodian.Workflow.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class EngagementsController : ControllerBase
{
    private readonly IEngagementRepository _repository;
    private readonly IAuditPublisher _auditPublisher;

    public EngagementsController(IEngagementRepository repository, IAuditPublisher auditPublisher)
    {
        _repository = repository;
        _auditPublisher = auditPublisher;
    }

    [HttpPost]
    public async Task<ActionResult<EngagementResponse>> CreateEngagement([FromBody] CreateEngagementRequest request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var (effectiveTenantId, isForbidden) = TryResolveTenantId(request.TenantId);
        if (isForbidden)
        {
            return Forbid();
        }

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

        // Subtask Genesis Event: Publish Genesis Event to Audit Service
        await _auditPublisher.PublishGenesisEventAsync(created, effectiveTenantId);

        return CreatedAtAction(nameof(GetEngagementById), new { id = created.EngagementId, tenantId = created.TenantId }, MapToResponse(created));
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<EngagementResponse>> GetEngagementById(Guid id, [FromQuery] string? tenantId)
    {
        var (effectiveTenantId, isForbidden) = TryResolveTenantId(tenantId);
        if (isForbidden)
        {
            return Forbid();
        }

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
        var (effectiveTenantId, isForbidden) = TryResolveTenantId(tenantId);
        if (isForbidden)
        {
            return Forbid();
        }

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

        var (effectiveTenantId, isForbidden) = TryResolveTenantId(request.TenantId);
        if (isForbidden)
        {
            return Forbid();
        }

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
        var (effectiveTenantId, isForbidden) = TryResolveTenantId(tenantId);
        if (isForbidden)
        {
            return Forbid();
        }

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
    /// Resolves tenant ID server-side from HttpContext JWT claims if authenticated.
    /// Strictly rejects cross-tenant requests where a caller specifies a different tenant ID than their JWT claim.
    /// Falls back to request parameter only in unauthenticated test contexts.
    /// </summary>
    private (string? TenantId, bool IsForbidden) TryResolveTenantId(string? requestTenantId)
    {
        var jwtTenantId = User?.FindFirst("tenant_id")?.Value ?? User?.FindFirst("tenantId")?.Value;

        if (!string.IsNullOrWhiteSpace(jwtTenantId))
        {
            var cleanJwtTenant = jwtTenantId.Trim();
            if (!string.IsNullOrWhiteSpace(requestTenantId) &&
                !string.Equals(requestTenantId.Trim(), cleanJwtTenant, StringComparison.OrdinalIgnoreCase))
            {
                // Cross-tenant access attempted by authenticated user -> 403 Forbidden
                return (null, true);
            }

            return (cleanJwtTenant, false);
        }

        return (!string.IsNullOrWhiteSpace(requestTenantId) ? requestTenantId.Trim() : null, false);
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
