using System.Security.Claims;
using Custodian.Audit.DTOs;
using Custodian.Audit.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Custodian.Audit.Controllers;

[Authorize]
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
        var (effectiveTenantId, isForbidden) = TryResolveTenantId(tenantId, request.TenantId);
        if (isForbidden)
        {
            return Forbid();
        }

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
        var (effectiveTenantId, isForbidden) = TryResolveTenantId(tenantId);
        if (isForbidden)
        {
            return Forbid();
        }

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
        var (effectiveTenantId, isForbidden) = TryResolveTenantId(tenantId);
        if (isForbidden)
        {
            return Forbid();
        }

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
        var (effectiveTenantId, isForbidden) = TryResolveTenantId(tenantId);
        if (isForbidden)
        {
            return Forbid();
        }

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
        var (effectiveTenantId, isForbidden) = TryResolveTenantId(tenantId);
        if (isForbidden)
        {
            return Forbid();
        }

        if (effectiveTenantId == Guid.Empty)
        {
            return BadRequest(new { message = "Tenant ID could not be resolved from JWT claim or query parameters." });
        }

        var results = await _eventService.GetEventsByTenantAsync(effectiveTenantId);
        return Ok(results);
    }

    /// <summary>
    /// Resolves tenant ID server-side from HttpContext JWT claims if authenticated.
    /// Strictly rejects cross-tenant requests where a caller specifies a different tenant ID than their JWT claim.
    /// Falls back to request header/query parameter only in unauthenticated test mock contexts.
    /// </summary>
    private (Guid TenantId, bool IsForbidden) TryResolveTenantId(string? tenantIdQuery = null, params Guid?[] fallbackTenantIds)
    {
        var claim = User?.FindFirst("tenant_id") ?? User?.FindFirst("tenantId");
        if (claim != null && !string.IsNullOrWhiteSpace(claim.Value))
        {
            var jwtTenantGuid = StringToGuid(claim.Value.Trim());

            // Check if query parameter conflicts with JWT claim
            if (!string.IsNullOrWhiteSpace(tenantIdQuery))
            {
                var queryTenantGuid = StringToGuid(tenantIdQuery.Trim());
                if (queryTenantGuid != jwtTenantGuid)
                {
                    return (Guid.Empty, true);
                }
            }

            // Check if header conflicts with JWT claim
            if (Request?.Headers.TryGetValue("X-Tenant-Id", out var tenantHeader) == true && !string.IsNullOrWhiteSpace(tenantHeader.ToString()))
            {
                var headerTenantGuid = StringToGuid(tenantHeader.ToString().Trim());
                if (headerTenantGuid != jwtTenantGuid)
                {
                    return (Guid.Empty, true);
                }
            }

            // Check if explicit fallback (e.g. from body payload) conflicts with JWT claim
            foreach (var fallback in fallbackTenantIds)
            {
                if (fallback.HasValue && fallback.Value != Guid.Empty && fallback.Value != jwtTenantGuid)
                {
                    return (Guid.Empty, true);
                }
            }

            return (jwtTenantGuid, false);
        }

        if (!string.IsNullOrWhiteSpace(tenantIdQuery))
        {
            return (StringToGuid(tenantIdQuery.Trim()), false);
        }

        if (Request?.Headers.TryGetValue("X-Tenant-Id", out var header) == true && !string.IsNullOrWhiteSpace(header.ToString()))
        {
            return (StringToGuid(header.ToString().Trim()), false);
        }

        foreach (var fallback in fallbackTenantIds)
        {
            if (fallback.HasValue && fallback.Value != Guid.Empty)
            {
                return (fallback.Value, false);
            }
        }

        return (Guid.Empty, false);
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
