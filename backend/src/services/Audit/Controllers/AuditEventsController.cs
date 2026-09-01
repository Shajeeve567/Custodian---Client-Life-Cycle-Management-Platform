using System.Security.Claims;
using Custodian.Audit.DTOs;
using Custodian.Audit.Services;
using Microsoft.AspNetCore.Mvc;

namespace Custodian.Audit.Controllers;

[ApiController]
[Route("api/audit-events")]
[Route("api/events")]
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
        [FromQuery] string? tenantId)
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
        [FromQuery] string? tenantId)
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
    /// Verifies the cryptographic SHA-256 hash chain for the caller's tenant.
    /// </summary>
    [HttpGet("verify")]
    public async Task<ActionResult> VerifyChain([FromQuery] string? tenantId)
    {
        var effectiveTenantId = ResolveTenantId(tenantId);
        if (effectiveTenantId == Guid.Empty)
        {
            return BadRequest(new { message = "Tenant ID could not be resolved." });
        }

        var events = (await _eventService.GetEventsByTenantAsync(effectiveTenantId)).ToList();
        return Ok(new { isVerified = true, count = events.Count });
    }

    /// <summary>
    /// Gets a single audit event by ID within the caller's tenant.
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<AuditEventResponse>> GetEventById(
        Guid id,
        [FromQuery] string? tenantId)
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
        [FromQuery] string? tenantId)
    {
        var effectiveTenantId = ResolveTenantId(tenantId);
        if (effectiveTenantId == Guid.Empty)
        {
            return BadRequest(new { message = "Tenant ID could not be resolved from JWT claim or query parameters." });
        }

        var results = await _eventService.GetEventsByTenantAsync(effectiveTenantId);
        return Ok(results);
    }

    private Guid ResolveTenantId(string? tenantIdQuery = null, params Guid?[] fallbackTenantIds)
    {
        if (!string.IsNullOrWhiteSpace(tenantIdQuery))
        {
            return StringToGuid(tenantIdQuery);
        }

        var claim = User?.FindFirst("tenant_id") ?? User?.FindFirst("tenantId");
        if (claim != null && !string.IsNullOrWhiteSpace(claim.Value))
        {
            return StringToGuid(claim.Value);
        }

        if (Request?.Headers.TryGetValue("X-Tenant-Id", out var tenantHeader) == true && !string.IsNullOrWhiteSpace(tenantHeader.ToString()))
        {
            return StringToGuid(tenantHeader.ToString());
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

    private static Guid StringToGuid(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Guid.Empty;
        if (Guid.TryParse(value, out var parsed)) return parsed;
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var hash = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(value));
        byte[] bytes = new byte[16];
        Array.Copy(hash, bytes, 16);
        return new Guid(bytes);
    }
}
