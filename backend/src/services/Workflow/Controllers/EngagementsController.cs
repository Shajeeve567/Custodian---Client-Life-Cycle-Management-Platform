using Custodian.Workflow.DTOs;
using Custodian.Workflow.Models;
using Custodian.Workflow.Repositories;
using Custodian.Workflow.Services;
using Custodian.Workflow.Services.Gates;
using Custodian.Workflow.Services.NextAction;
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
    private readonly IGateEvaluator _gateEvaluator;
    private readonly IClientActionService? _actionService;
    private readonly INextActionService? _nextActionService;

    public EngagementsController(
        IEngagementRepository repository,
        IAuditPublisher auditPublisher,
        IGateEvaluator gateEvaluator,
        IClientActionService? actionService = null,
        INextActionService? nextActionService = null)
    {
        _repository = repository;
        _auditPublisher = auditPublisher;
        _gateEvaluator = gateEvaluator;
        _actionService = actionService;
        _nextActionService = nextActionService;
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
            Stage = EngagementStage.Onboarding,
            CreatedAt = DateTime.UtcNow
        };

        var created = await _repository.CreateAsync(engagement);

        // Seed default 5-stage lifecycle actions for the new engagement
        if (_actionService != null)
        {
            try
            {
                await _actionService.EnsureLifecycleActionsAsync(created.EngagementId, effectiveTenantId);
            }
            catch
            {
                // Fallback gracefully
            }
        }

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

    /// <summary>
    /// CSTD-19 (19-N2): Deterministically evaluates and returns the highest-priority next action
    /// and ordered blockers for staff/owner workspace view.
    /// </summary>
    [HttpGet("{id}/next-action")]
    [Authorize(Roles = "Owner,Staff")]
    public async Task<ActionResult<NextActionResult>> GetNextAction(
        Guid id,
        [FromQuery] string? tenantId,
        CancellationToken ct = default)
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
            return NotFound(new { message = $"Engagement '{id}' was not found in tenant '{effectiveTenantId}'." });
        }

        if (_nextActionService == null)
        {
            return StatusCode(500, new { message = "Next action evaluation service is not configured." });
        }

        var result = await _nextActionService.GetNextActionAsync(id, effectiveTenantId, NextActionView.Staff, ct);
        if (result == null)
        {
            return NotFound(new { message = $"Next action could not be evaluated for engagement '{id}'." });
        }

        return Ok(result);
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

    [HttpPut("{id}/stage")]
    public async Task<ActionResult<EngagementResponse>> UpdateStage(Guid id, [FromBody] UpdateEngagementStageRequest request)
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

        // Stage/Status guard: a Closed or Cancelled engagement is terminal and its stage cannot move.
        if (engagement.Status is EngagementStatus.Closed or EngagementStatus.Cancelled)
        {
            return Conflict(new
            {
                message = $"Engagement in status '{engagement.Status}' cannot advance stage."
            });
        }

        if (!Enum.TryParse<EngagementStage>(request.Stage, true, out var newStage))
        {
            return BadRequest($"Invalid stage: '{request.Stage}'. Valid stages are: {string.Join(", ", Enum.GetNames<EngagementStage>())}.");
        }

        // Subtask Lifecycle Validation: Enforce sequential, forward-only stage transitions
        if (!EngagementStageValidator.IsValidTransition(engagement.Stage, newStage))
        {
            return BadRequest(new
            {
                message = $"Invalid stage transition from '{engagement.Stage}' to '{newStage}'."
            });
        }

        // Subtask CSTD-18: Gate Evaluation — mandatory gates (required documents, and in
        // future Approval/Payment conditions) must be satisfied before the transition
        // proceeds. Runs after the cheap in-memory checks above, since it may call out to
        // the Documents service.
        var gateResult = await _gateEvaluator.EvaluateAsync(engagement.EngagementId, effectiveTenantId, newStage);
        if (!gateResult.IsSatisfied)
        {
            return BadRequest(new
            {
                message = gateResult.Reason,
                requirements = gateResult.Requirements
            });
        }

        var previousStage = engagement.Stage;
        engagement.Stage = newStage;

        var updated = await _repository.UpdateAsync(engagement);
        int newStageNumber = (int)newStage + 1;

        if (_actionService != null)
        {
            await _actionService.ActivateStageActionsAsync(updated.EngagementId, effectiveTenantId, newStageNumber);
        }

        // Subtask Audit: Publish Stage Change Event to Audit Service
        await _auditPublisher.PublishEventAsync(
            updated.EngagementId,
            effectiveTenantId,
            "System",
            "StageChange",
            new
            {
                fromStage = previousStage.ToString(),
                toStage = newStage.ToString(),
                changedAt = DateTime.UtcNow
            });

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
        Stage = e.Stage.ToString(),
        StageProgressPercentage = EngagementStageValidator.GetProgressPercentage(e.Stage),
        CreatedAt = e.CreatedAt,
        ClosedAt = e.ClosedAt
    };
}
