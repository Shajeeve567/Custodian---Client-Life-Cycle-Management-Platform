using System.Security.Claims;
using Custodian.Audit.DTOs;
using Custodian.Audit.Services;
using Microsoft.AspNetCore.Mvc;

namespace Custodian.Audit.Controllers;

[ApiController]
[Route("api/audit-events")]
public class AuditEventsController : ControllerBase
{
    private readonly IAuditEventService _eventService;

    public AuditEventsController(IAuditEventService eventService)
    {
        _eventService = eventService;
    }

    /// <summary>
    /// Ingests a new append-only domain event.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<AuditEventResponse>> CreateEvent(
        [FromBody] CreateAuditEventRequest request,
        [FromQuery] Guid? tenantId)
    {
        var effectiveTenantId = ResolveTenantId(tenantId, request.TenantId);
        if (effectiveTenantId == Guid.Empty)
        {
            return BadRequest(new { message = "Tenant ID could not be resolved from JWT claim or query parameters." });
        }

        try
        {
            var result = await _eventService.RecordEventAsync(request, effectiveTenantId);
            return CreatedAtAction(nameof(GetEventById), new { id = result.EventId, tenantId = effectiveTenantId }, result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Gets all audit events for a specific engagement within the caller's tenant.
    /// </summary>
    [HttpGet("engagement/{engagementId:guid}")]
    public async Task<ActionResult<IEnumerable<AuditEventResponse>>> GetEventsByEngagement(
        Guid engagementId,
        [FromQuery] Guid? tenantId)
    {
        var effectiveTenantId = ResolveTenantId(tenantId);
        if (effectiveTenantId == Guid.Empty)
        {
            return BadRequest(new { message = "Tenant ID could not be resolved from JWT claim or query parameters." });
        }

        var results = await _eventService.GetEventsByEngagementAsync(engagementId, effectiveTenantId);
        return Ok(results);
    }

    /// <summary>
    /// Gets a single audit event by ID within the caller's tenant.
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<AuditEventResponse>> GetEventById(
        Guid id,
        [FromQuery] Guid? tenantId)
    {
        var effectiveTenantId = ResolveTenantId(tenantId);
        if (effectiveTenantId == Guid.Empty)
        {
            return BadRequest(new { message = "Tenant ID could not be resolved from JWT claim or query parameters." });
        }

        var result = await _eventService.GetEventByIdAsync(id, effectiveTenantId);
        if (result == null)
        {
            return NotFound(new { message = $"Audit event with ID '{id}' was not found for tenant '{effectiveTenantId}'." });
        }

        return Ok(result);
    }

    /// <summary>
    /// Gets all audit events for the caller's tenant.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<AuditEventResponse>>> GetEvents(
        [FromQuery] Guid? tenantId)
    {
        var effectiveTenantId = ResolveTenantId(tenantId);
        if (effectiveTenantId == Guid.Empty)
        {
            return BadRequest(new { message = "Tenant ID could not be resolved from JWT claim or query parameters." });
        }

        var results = await _eventService.GetEventsByTenantAsync(effectiveTenantId);
        return Ok(results);
    }

    /// <summary>
    /// Helper method to extract tenant ID from JWT claims, falling back to optional parameter.
    /// </summary>
    private Guid ResolveTenantId(params Guid?[] fallbackTenantIds)
    {
        var claim = User?.FindFirst("tenant_id") ?? User?.FindFirst("tenantId");
        if (claim != null && Guid.TryParse(claim.Value, out var jwtTenantId))
        {
            return jwtTenantId;
        }

        foreach (var fallback in fallbackTenantIds)
        {
            if (fallback.HasValue && fallback.Value != Guid.Empty)
            {
                return fallback.Value;
            }
        }

        return Guid.Empty;
    }
}
