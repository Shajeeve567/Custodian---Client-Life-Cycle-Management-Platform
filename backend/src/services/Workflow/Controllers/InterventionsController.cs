using Custodian.Workflow.DTOs;
using Custodian.Workflow.Repositories;
using Custodian.Workflow.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Custodian.Workflow.Controllers;

[Authorize]
[ApiController]
[Route("api/engagements/{engagementId:guid}/interventions")]
public class InterventionsController : ControllerBase
{
    private readonly IInterventionService _interventions;
    private readonly IEngagementRepository _engagements;

    public InterventionsController(
        IInterventionService interventions,
        IEngagementRepository engagements)
    {
        _interventions = interventions;
        _engagements = engagements;
    }

    [HttpPost]
    [Authorize(Roles = "Owner,Staff")]
    public async Task<ActionResult<InterventionResponse>> Record(
        [FromRoute] Guid engagementId,
        [FromBody] RecordInterventionRequest request,
        [FromQuery] string? tenantId,
        CancellationToken ct = default)
    {
        var (effectiveTenantId, isForbidden) = TryResolveTenantId(tenantId);
        if (isForbidden) return Forbid();
        if (string.IsNullOrWhiteSpace(effectiveTenantId))
            return BadRequest(new { message = "Tenant identification is required." });

        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var engagement = await _engagements.GetByIdAsync(engagementId, effectiveTenantId);
        if (engagement == null)
            return NotFound(new { message = $"Engagement '{engagementId}' not found in tenant." });

        try
        {
            var result = await _interventions.RecordAsync(
                engagementId, effectiveTenantId, ResolveActor(), request, ct);

            return CreatedAtAction(
                nameof(List),
                new { engagementId, tenantId = effectiveTenantId },
                result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet]
    [Authorize(Roles = "Owner,Staff")]
    public async Task<ActionResult<IReadOnlyList<InterventionResponse>>> List(
        [FromRoute] Guid engagementId,
        [FromQuery] string? tenantId,
        CancellationToken ct = default)
    {
        var (effectiveTenantId, isForbidden) = TryResolveTenantId(tenantId);
        if (isForbidden) return Forbid();
        if (string.IsNullOrWhiteSpace(effectiveTenantId))
            return BadRequest(new { message = "Tenant identification is required." });

        var items = await _interventions.ListForEngagementAsync(engagementId, effectiveTenantId, ct);
        return Ok(items);
    }

    private string ResolveActor() =>
        User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
        ?? User?.FindFirst("sub")?.Value
        ?? "System";

    private (string? TenantId, bool IsForbidden) TryResolveTenantId(string? requestTenantId)
    {
        var jwtTenantId = User?.FindFirst("tenant_id")?.Value ?? User?.FindFirst("tenantId")?.Value;

        if (!string.IsNullOrWhiteSpace(jwtTenantId))
        {
            var cleanJwtTenant = jwtTenantId.Trim();
            if (!string.IsNullOrWhiteSpace(requestTenantId) &&
                !string.Equals(requestTenantId.Trim(), cleanJwtTenant, StringComparison.OrdinalIgnoreCase))
            {
                return (null, true);
            }
            return (cleanJwtTenant, false);
        }

        return (!string.IsNullOrWhiteSpace(requestTenantId) ? requestTenantId.Trim() : null, false);
    }
}